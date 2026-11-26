using OpenRigs.Core;
using OpenRigs.Trials;
using System.Collections.Generic;
using System.Linq;

// The generated task types (UclOpenCoenDomeDecisionMakingTask.Generated.cs) as lib-open-rigs' trial-condition code reads them.
namespace UclOpenCoenDomeDecisionMakingTask
{
    /// <summary>The task file's curriculum block, checked by OpenRigs.Core CurriculumCheck.</summary>
    partial class Curriculum : ICurriculum
    {
    }

    /// <summary>One of the task's trialConditions, built by OpenRigs.Trials TrialConditions.</summary>
    partial class TrialCondition : ITrialCondition
    {
    }

    /// <summary>The task's parameters, whose trials are drawn from its trialConditions.</summary>
    partial class UclOpenCoenDomeDecisionMakingTaskParameters : IConditionedTask
    {
        IList<ITrialCondition> IConditionedTask.Conditions { get { return TrialConditions == null ? null : TrialConditions.Cast<ITrialCondition>().ToList(); } }
        ICurriculum IConditionedTask.Stage { get { return Curriculum; } }
        bool IConditionedTask.OptogeneticsEnabled { get { return Optogenetics != null && Optogenetics.On; } }
    }

    /// <summary>The optogenetics block, whose two boxes choose the session's Zapit: zapitBonsai (Zapit-Bonsai, ZapitDaq on the NI
    /// card) or zapitMatlab (Zapit-MATLAB, MATLAB Zapit over TCP; goes when MATLAB Zapit is retired), never both (TrialDraw.BothZapits).</summary>
    partial class Optogenetics
    {
        /// <summary>Whether this is an optogenetics experiment: either box ticked. Not a task field, so never written to Settings_Task.</summary>
        [Newtonsoft.Json.JsonIgnore]
        [YamlDotNet.Serialization.YamlIgnore]
        public bool On { get { return ZapitBonsai || ZapitMatlab; } }
    }
}
