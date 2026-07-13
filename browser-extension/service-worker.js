"use strict";

const NATIVE_HOST = "com.mkm.suavo.browser_observer";
const PROTOCOL = "suavo-native-messaging-v1";
const VERSION = 1;
const MAX_SESSION_MS = 60 * 60 * 1000;
const encoder = new TextEncoder();

let port = null;
let session = null;
let inFlight = null;
let pendingHostname = null;
let reconnectDelayMs = 1000;
let reconnectTimer = null;

function isExactObject(value, expectedKeys) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return false;
  const keys = Object.keys(value).sort();
  return keys.length === expectedKeys.length &&
    keys.every((key, index) => key === [...expectedKeys].sort()[index]);
}

function decodeBase64Url(value, exactBytes) {
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]+$/.test(value)) return null;
  try {
    const padded = value.replace(/-/g, "+").replace(/_/g, "/") +
      "=".repeat((4 - (value.length % 4)) % 4);
    const binary = atob(padded);
    if (binary.length !== exactBytes) return null;
    return Uint8Array.from(binary, character => character.charCodeAt(0));
  } catch {
    return null;
  }
}

function encodeBase64Url(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function fixedAsciiEquals(left, right) {
  if (typeof left !== "string" || typeof right !== "string" || left.length !== right.length) {
    return false;
  }
  let difference = 0;
  for (let index = 0; index < left.length; index += 1) {
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  }
  return difference === 0;
}

async function hmac(key, canonical) {
  const cryptoKey = await crypto.subtle.importKey(
    "raw",
    key,
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"]
  );
  return encodeBase64Url(new Uint8Array(
    await crypto.subtle.sign("HMAC", cryptoKey, encoder.encode(canonical))
  ));
}

function normalizeActiveTabHostname(url) {
  if (typeof url !== "string" || url.length > 4096) return null;
  try {
    const parsed = new URL(url);
    if (parsed.protocol !== "https:" && parsed.protocol !== "http:") return null;
    let hostname = parsed.hostname.toLowerCase().replace(/\.$/, "");
    if (hostname.startsWith("[") && hostname.endsWith("]")) {
      hostname = hostname.slice(1, -1);
    }
    if (!hostname || hostname.length > 253 || /[\s/\\@?#%]/.test(hostname)) return null;
    return hostname;
  } catch {
    return null;
  }
}

async function acceptHello(message) {
  const expected = [
    "version", "type", "protocol", "sessionId", "sessionKey",
    "challenge", "counter", "expiresAtUnixMs"
  ];
  if (session !== null || !isExactObject(message, expected) ||
      message.version !== VERSION || message.type !== "hello" ||
      message.protocol !== PROTOCOL || message.counter !== 0 ||
      !Number.isSafeInteger(message.expiresAtUnixMs) ||
      message.expiresAtUnixMs <= Date.now() ||
      message.expiresAtUnixMs > Date.now() + MAX_SESSION_MS) {
    disconnect();
    return;
  }
  const key = decodeBase64Url(message.sessionKey, 32);
  const challenge = decodeBase64Url(message.challenge, 32);
  const sessionId = decodeBase64Url(message.sessionId, 16);
  if (key === null || challenge === null || sessionId === null) {
    disconnect();
    return;
  }

  session = {
    id: message.sessionId,
    key,
    challenge: message.challenge,
    counter: 0,
    expiresAtUnixMs: message.expiresAtUnixMs
  };
  reconnectDelayMs = 1000;
  await captureFocusedTab();
  await pump();
}

async function acceptAcknowledgement(message) {
  const expected = [
    "version", "type", "protocol", "sessionId", "counter",
    "nextChallenge", "status", "mac"
  ];
  if (session === null || inFlight === null || !isExactObject(message, expected) ||
      message.version !== VERSION || message.type !== "accepted" ||
      message.protocol !== PROTOCOL || message.status !== "ready" ||
      message.sessionId !== session.id || message.counter !== inFlight.counter ||
      decodeBase64Url(message.nextChallenge, 32) === null) {
    disconnect();
    return;
  }

  const canonical = [
    PROTOCOL,
    session.id,
    "accepted",
    String(message.counter),
    message.nextChallenge,
    "ready"
  ].join("\n");
  const expectedMac = await hmac(session.key, canonical);
  if (!fixedAsciiEquals(expectedMac, message.mac)) {
    disconnect();
    return;
  }

  session.counter = message.counter;
  session.challenge = message.nextChallenge;
  inFlight = null;
  await pump();
}

async function acceptFatal(message) {
  const expected = ["version", "type", "protocol", "sessionId", "counter", "reason", "mac"];
  if (session === null || !isExactObject(message, expected) ||
      message.version !== VERSION || message.type !== "fatal" ||
      message.protocol !== PROTOCOL || message.sessionId !== session.id ||
      !Number.isSafeInteger(message.counter) || typeof message.reason !== "string" ||
      !/^[a-z][a-z0-9_]{0,63}$/.test(message.reason)) {
    disconnect();
    return;
  }
  const canonical = [
    PROTOCOL,
    session.id,
    "fatal",
    String(message.counter),
    message.reason,
    "degraded"
  ].join("\n");
  const expectedMac = await hmac(session.key, canonical);
  if (!fixedAsciiEquals(expectedMac, message.mac)) {
    disconnect();
    return;
  }
  disconnect();
}

async function onNativeMessage(message) {
  try {
    if (message?.type === "hello") await acceptHello(message);
    else if (message?.type === "accepted") await acceptAcknowledgement(message);
    else if (message?.type === "fatal") await acceptFatal(message);
    else disconnect();
  } catch {
    disconnect();
  }
}

function connect() {
  if (port !== null) return;
  try {
    const candidate = chrome.runtime.connectNative(NATIVE_HOST);
    port = candidate;
    candidate.onMessage.addListener(message => { void onNativeMessage(message); });
    candidate.onDisconnect.addListener(() => {
      if (port === candidate) {
        port = null;
        session = null;
        inFlight = null;
        scheduleReconnect();
      }
    });
  } catch {
    port = null;
    scheduleReconnect();
  }
}

function disconnect() {
  const current = port;
  port = null;
  session = null;
  inFlight = null;
  try { current?.disconnect(); } catch { }
  scheduleReconnect();
}

function scheduleReconnect() {
  if (reconnectTimer !== null) return;
  const delay = reconnectDelayMs;
  reconnectDelayMs = Math.min(reconnectDelayMs * 2, 60000);
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, delay);
}

async function pump() {
  if (port === null || session === null || inFlight !== null || pendingHostname === null) return;
  if (Date.now() >= session.expiresAtUnixMs) {
    disconnect();
    return;
  }

  const hostname = pendingHostname;
  pendingHostname = null;
  const counter = session.counter + 1;
  if (!Number.isSafeInteger(counter)) {
    disconnect();
    return;
  }
  const canonical = [PROTOCOL, session.id, String(counter), session.challenge, hostname].join("\n");
  const mac = await hmac(session.key, canonical);
  inFlight = { counter };
  port.postMessage({
    version: VERSION,
    type: "active_tab_hostname",
    protocol: PROTOCOL,
    sessionId: session.id,
    counter,
    challenge: session.challenge,
    hostname,
    mac
  });
}

function queueTab(tab) {
  if (tab?.active !== true) return;
  const hostname = normalizeActiveTabHostname(tab.url);
  if (hostname === null) return;
  pendingHostname = hostname;
  void pump();
}

async function captureFocusedTab() {
  try {
    const tabs = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (tabs.length === 1) queueTab(tabs[0]);
  } catch { }
}

chrome.tabs.onActivated.addListener(async activeInfo => {
  try { queueTab(await chrome.tabs.get(activeInfo.tabId)); } catch { }
});

chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (tab.active && (typeof changeInfo.url === "string" || changeInfo.status === "complete")) {
    queueTab(tab);
  }
});

chrome.windows.onFocusChanged.addListener(() => { void captureFocusedTab(); });
chrome.runtime.onStartup.addListener(() => { connect(); void captureFocusedTab(); });
chrome.runtime.onInstalled.addListener(() => { connect(); void captureFocusedTab(); });

connect();
void captureFocusedTab();
