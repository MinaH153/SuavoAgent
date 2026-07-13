"""Immutable reviewed legal catalogs used by the release-bundle generator."""

EXPECTED_VENDORED_SOURCE_LICENSES = {
    "dotnet/jsoncanonicalizer/JsonCanonicalizer.cs": "Apache-2.0",
    "dotnet/es6numberserializer/NumberCachedPowers.cs": "BSD-3-Clause",
    "dotnet/es6numberserializer/NumberDToA.cs": (
        "MPL-2.0 AND LicenseRef-Lucent-DToA"
    ),
    "dotnet/es6numberserializer/NumberDiyFp.cs": "BSD-3-Clause",
    "dotnet/es6numberserializer/NumberDoubleHelper.cs": "BSD-3-Clause",
    "dotnet/es6numberserializer/NumberFastDToA.cs": "BSD-3-Clause",
    "dotnet/es6numberserializer/NumberFastDToABuilder.cs": "MPL-2.0",
    "dotnet/es6numberserializer/NumberToJson.cs": "Apache-2.0",
}
EXPECTED_VENDORED_UPSTREAM_SHA256 = {
    "dotnet/jsoncanonicalizer/JsonCanonicalizer.cs": "234982a675b3d6ff12522bcbd823d3e6e0ccbe543029a6ff094da7401a844a29",
    "dotnet/es6numberserializer/NumberCachedPowers.cs": "659174e3f8b2a3c69435538bbf30fa8781a584a209f1a962cd5514a8c0d1fd79",
    "dotnet/es6numberserializer/NumberDToA.cs": "f6c215b6787efa3871cf581c08cd645520066704e5a78a9c02856d5c448688cb",
    "dotnet/es6numberserializer/NumberDiyFp.cs": "be3a14af2fec6086f234e3cf06a90edb98cbdbef58447432485b3cac1684c3b5",
    "dotnet/es6numberserializer/NumberDoubleHelper.cs": "898c6ceee1025af51ba6abdd2111bc24e209857a23d53601edf699db9a1612df",
    "dotnet/es6numberserializer/NumberFastDToA.cs": "cbe35d5ed16acd05889d6fe9ffd052964cf06ffd831b13f21bfec61f6d548dc3",
    "dotnet/es6numberserializer/NumberFastDToABuilder.cs": "82b39967c220a5bed069b36839a7d7993e907a668cc03b356939fd3f79b3742a",
    "dotnet/es6numberserializer/NumberToJson.cs": "aecd29efdaa1a193e43d5535d4efeb294314b5cbd436c70a8399b6040a0b0f6a",
}
EXPECTED_VENDORED_LOCAL_FILES = {
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/JsonCanonicalizer.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberCachedPowers.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.Infrastructure.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.Formatting.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDiyFp.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDoubleHelper.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberFastDToA.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberFastDToABuilder.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberToJson.cs",
}
EXPECTED_VENDORED_LOCAL_SHA256 = {
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/JsonCanonicalizer.cs": "9d65a73f8bf827c46176443923fac47a6b70c9de09ed67552e838ea61ec053a0",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberCachedPowers.cs": "659174e3f8b2a3c69435538bbf30fa8781a584a209f1a962cd5514a8c0d1fd79",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.Infrastructure.cs": "521039e11a17b8e1eb7554384256f7eea094b2a9e664b7e6de00e7875042dcbd",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.cs": "831e398c5b261ea83b218e8daf940ba27f355e28d4ec525742ea1be5322c77cc",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.Formatting.cs": "98ebd50749d1f761c6b3c6274f06fc67e29889c950bce97e04de94b44ae1bba8",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDiyFp.cs": "be3a14af2fec6086f234e3cf06a90edb98cbdbef58447432485b3cac1684c3b5",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDoubleHelper.cs": "898c6ceee1025af51ba6abdd2111bc24e209857a23d53601edf699db9a1612df",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberFastDToA.cs": "d7c40f028e780fa25baa56f30b5503db2079726d158957ed444d32a66132adb2",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberFastDToABuilder.cs": "82b39967c220a5bed069b36839a7d7993e907a668cc03b356939fd3f79b3742a",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberToJson.cs": "e13631db1fca0758e187eafae4c67c43e40b71b5ebbef8f1cc592def6fd2b1b8",
}
EXPECTED_VENDORED_LICENSE_FILES = {
    "legal/license-texts/JsonCanonicalization-Apache-NOTICE.txt": "8c2088b148c0f4479ea3f2f627faae2565431e1b35dd9b0536164e4becbbea5c",
    "legal/license-texts/Apache-2.0.txt": "8f10c66b475de2809a92847d38807c34f137f0cf2135a27bea64142595681bc3",
    "legal/license-texts/MPL-2.0.txt": "fab3dd6bdab226f1c08630b1dd917e11fcb4ec5e1e020e2c16f83a0a13863e85",
    "legal/license-texts/V8-DToA-BSD-3-Clause.txt": "b14d84bbb6b81230a9464568a781736f78fd0fcd393f2072bf373c2a1f96f56c",
    "legal/license-texts/NumberDToA-NOTICE.txt": "f9f33436e10402b38569af2acf6b54a5dbd4077f87ecce5593def6518552a002",
}
EXPECTED_EXTERNAL_ASSETS = {
    "pharmacist-panda": (
        None,
        "MKM proprietary; OpenAI-generated original asset",
        True,
    ),
    "qwen3-1.7b-q4-k-m": (
        "https://huggingface.co/Qwen/Qwen3-1.7B-GGUF",
        "Apache-2.0",
        True,
    ),
    "llamasharp-backend-cpu-0.24.0": (
        "https://github.com/SciSharp/LLamaSharp",
        "MIT",
        True,
    ),
    "tesseract-native-5.2.0-eng": (
        "https://github.com/charlesw/tesseract",
        "Apache-2.0 AND BSD-2-Clause",
        True,
    ),
}
