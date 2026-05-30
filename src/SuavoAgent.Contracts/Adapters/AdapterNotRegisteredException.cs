using System;

namespace SuavoAgent.Contracts.Adapters;

/// <summary>Thrown when <see cref="IAdapterRegistry.Resolve"/> is asked for an unregistered PMS family.</summary>
public sealed class AdapterNotRegisteredException : Exception
{
    public string AdapterType { get; }

    public AdapterNotRegisteredException(string adapterType)
        : base($"No adapter registered for type '{adapterType}'.")
        => AdapterType = adapterType;
}
