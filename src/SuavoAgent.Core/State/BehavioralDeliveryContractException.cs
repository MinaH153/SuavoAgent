namespace SuavoAgent.Core.State;

internal sealed class BehavioralDeliveryContractException : Exception
{
    internal const string StreamChannelMismatchCode = "behavioral_stream_channel_mismatch";
    internal const string StreamPartialOverlapCode = "behavioral_stream_partial_overlap";
    internal const string DroppedTotalRegressionCode = "behavioral_dropped_total_regression";

    private BehavioralDeliveryContractException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }

    internal string ErrorCode { get; }

    internal static BehavioralDeliveryContractException StreamChannelMismatch() =>
        new(StreamChannelMismatchCode);

    internal static BehavioralDeliveryContractException StreamPartialOverlap() =>
        new(StreamPartialOverlapCode);

    internal static BehavioralDeliveryContractException DroppedTotalRegression() =>
        new(DroppedTotalRegressionCode);
}
