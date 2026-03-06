using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Text;
using NimBus.Core.Events;

namespace NimBus.Events.SurveyMonkey
{
    [Description("Triggers when the survey is completed")]
    public class SurveyUpdated : Event
    {
        public static SurveyUpdated Example = new SurveyUpdated()
        {
            SurveyId = 123456789,
            CreatedOn = DateTime.Now,
            Comment = "Survey comment",
            ResponseId = 123456,
            SatisfactionWeight = 10,
            NPS = 1,
            Availability = 10,
            Reliability = 10,
            AfterSale = 10,
            Accessibility = 10,
            Service = 10,
            Expertise = 10,
            Ordering = 10,
            Information = 10,
            Pricing = 10,
            Status = "Completed",
            Type = "Satisfaction 2.0",
            ContactKey = "DK - 123456 - 2",
            CustomerKey = "DK - 123456"
        };

        [Required]
        [Description("Unique identifier for the survey")]
        public long? SurveyId { get; set; }

        [Description("The Survey created date")]
        public DateTime CreatedOn { get; set; }

        [Description("Survey comment")]
        public string Comment { get; set; }

        [Description("Id on the response")]
        public long? ResponseId { get; set; }

        [Description("Score for SatisfactionWeight")]
        public int? SatisfactionWeight { get; set; }

        [Description("NPS value for satisfaction weight")]
        public int NPS { get; set; }

        [Description("Score for Availability")]
        public int? Availability { get; set; }

        [Description("Score for Reliability")]
        public int? Reliability { get; set; }

        [Description("Score for AfterSale")]
        public int? AfterSale { get; set; }

        [Description("Score for Accessibility")]
        public int? Accessibility { get; set; }

        [Description("Score for Service")]
        public int? Service { get; set; }

        [Description("Score for Expertise")]
        public int? Expertise { get; set; }

        [Description("Score for Ordering")]
        public int? Ordering { get; set; }

        [Description("Score for Information")]
        public int? Information { get; set; }

        [Description("Score for Pricing")]
        public int? Pricing { get; set; }

        [Description("Status for survey")]
        public string Status { get; set; }

        [Description("Type of survey")]
        public string Type { get; set; }

        [Description("Contact key from survey")]
        public string ContactKey { get; set; }

        [Description("Customer key from survey")]
        public string CustomerKey { get; set; }

        public override string GetSessionId() => ResponseId.ToString();
    }
}
