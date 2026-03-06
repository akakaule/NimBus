using NimBus.Core.Events;
using System.ComponentModel;

namespace NimBus.Events.WebInquiry
{
    [Description("Triggers whenever a web inquiry is created from the Website")]
    public class WebInquiryCreated : Event
    {
        public static WebInquiryCreated Example = new WebInquiryCreated()
        {
            Campaign = "Axis Q2 25",
            ContactHash = "397efcc2-8dc0-43ba-a0eb-201b5c03b0c1",
            Customer = "313123145",
            Source = "Web",
            CampaignLink = "https://www.eetgroup.com/brands/axis",
            BusinessUnit = "EET UK",
            BusinessLine = "Surveillance & Security",
            Brand = "Axis",
            PrivateLabel = true,
            Comment = "Looking forward to exploring this opportunity."
        };

        [Description("Campaign name")]
        public string Campaign { get; set; }

        [Description("Contact hash")]
        public string ContactHash { get; set; }

        [Description("Customer number")]
        public string Customer { get; set; }

        [Description("Origin")]
        public string Source { get; set; }

        [Description("Campaign url")]
        public string CampaignLink { get; set; }

        [Description("Business unit")]
        public string BusinessUnit { get; set; }

        [Description("Business line")]
        public string BusinessLine { get; set; }

        [Description("Brand")]
        public string Brand { get; set; }

        [Description("Private label flag")]
        public bool PrivateLabel { get; set; }

        [Description("Comment")]
        public string Comment { get; set; }
    }
}
