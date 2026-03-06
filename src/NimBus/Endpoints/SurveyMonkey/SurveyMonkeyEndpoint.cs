using NimBus.Core.Endpoints;
using NimBus.Events.SurveyMonkey;
using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.Endpoints.SurveyMonkey
{
    public class SurveyMonkeyEndpoint : Endpoint
    {
        public SurveyMonkeyEndpoint()
        {
            Produces<SurveyCreated>();
            Produces<SurveyUpdated>();
        }
        public override ISystem System => new SurveyMonkeySystem();
        public override string Description => "Publishes Survey Monkey events. Triggered by Survey Monkey webhook. Consumes events by calling HTTP triggered function. Runs in an Azure Function.";
    }
}
