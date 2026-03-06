using NimBus.Core;
using NimBus.Endpoints.CRM;
using NimBus.Endpoints.Website;
using NimBus.Endpoints.NAV09;
using NimBus.Endpoints.SurveyMonkey;

namespace NimBus
{
    public class PlatformConfiguration : Platform
    {
        public PlatformConfiguration()
        {
            AddEndpoint(new Nav09Endpoint());
            AddEndpoint(new CrmEndpoint());
            AddEndpoint(new CrmBulkEndpoint());
            AddEndpoint(new WebEndpoint());
            AddEndpoint(new SurveyMonkeyEndpoint());
        }
    }
}
