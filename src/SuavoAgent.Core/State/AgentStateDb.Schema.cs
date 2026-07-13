using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void InitSchema()
    {
        InitializeSchemaFoundation();
        InitializeLearningAndCanarySchema();
        InitializeBehavioralAndFeedbackSchema();
        InitializeUniversalObservationSchema();
        InitializePricingAndAutonomySchema();
        ApplyEarlyVersionedMigrations();
        ApplyLateVersionedMigrations();
    }
}
