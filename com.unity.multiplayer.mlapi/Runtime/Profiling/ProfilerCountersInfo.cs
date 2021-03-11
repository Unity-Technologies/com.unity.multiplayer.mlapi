#if UNITY_2020_2_OR_NEWER
using Unity.Profiling;
#endif
using UnityEngine;

namespace MLAPI.Profiling
{
    internal static class ProfilerCountersInfo
    {
#if UNITY_2020_2_OR_NEWER && ENABLE_PROFILER
        // Operations
        private static readonly ProfilerCounterValue<int> k_ConnectionsCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberOfConnections,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_TickRateCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.ReceiveTickRate,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        // Messages
        private static readonly ProfilerCounterValue<int> k_NamedMessagesCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberOfNamedMessages,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_UnnamedMessagesCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberOfUnnamedMessages,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_BytesSentCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberBytesSent,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_BytesReceivedCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberBytesReceived,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_NetworkVarsCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberNetworkVarsReceived,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        // RPCs
        private static readonly ProfilerCounterValue<int> k_RPCsSentCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberOfRPCsSent,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_RPCsReceivedCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberOfRPCsReceived,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_RPCBatchesSentCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberOfRPCBatchesSent,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_RPCBatchesReceivedCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberOfRPCBatchesReceived,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_RPCQueueProcessedCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberOfRPCQueueProcessed,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_RPCsInQueueSizeCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberOfRPCsInQueueSize,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static readonly ProfilerCounterValue<int> k_RPCsOutQueueSizeCounterValue =
            new ProfilerCounterValue<int>(ProfilerCategory.Network, ProfilerConstants.NumberOfRPCsOutQueueSize,
                ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        [RuntimeInitializeOnLoadMethod]
        private static void RegisterMLAPIPerformanceEvent()
        {
            NetworkManager.OnPerformanceDataEvent += OnPerformanceTickData;
        }

        private static void OnPerformanceTickData(PerformanceTickData tickData)
        {
            // Operations
            k_ConnectionsCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfConnections);
            k_TickRateCounterValue.Value = tickData.GetData(ProfilerConstants.ReceiveTickRate);

            // Messages
            k_NamedMessagesCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfNamedMessages);
            k_UnnamedMessagesCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfUnnamedMessages);
            k_BytesSentCounterValue.Value = tickData.GetData(ProfilerConstants.NumberBytesSent);
            k_BytesReceivedCounterValue.Value = tickData.GetData(ProfilerConstants.NumberBytesReceived);
            k_NetworkVarsCounterValue.Value = tickData.GetData(ProfilerConstants.NumberNetworkVarsReceived);

            // RPCs
            k_RPCsSentCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfRPCsSent);
            k_RPCsReceivedCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfRPCsReceived);
            k_RPCBatchesSentCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfRPCBatchesSent);
            k_RPCBatchesReceivedCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfRPCBatchesReceived);
            k_RPCBatchesReceivedCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfRPCBatchesReceived);
            k_RPCQueueProcessedCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfRPCQueueProcessed);
            k_RPCsInQueueSizeCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfRPCsInQueueSize);
            k_RPCsOutQueueSizeCounterValue.Value = tickData.GetData(ProfilerConstants.NumberOfRPCsOutQueueSize);
        }
#endif
    }
}
