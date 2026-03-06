using NimBus.Core.Events;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Events.Contacts
{
    [Description("Triggers whenever a contact is deactivated in CRM")]
    public class ContactDeactivated : Event
    {
        public static ContactDeactivated Example = new ContactDeactivated()
        {
            ContactId = Guid.NewGuid(),
            DeactivatedOn = DateTime.Now,
        };

        [Required]
        [Description("The CRM contact record unique identifier")]
        public Guid ContactId { get; set; }
        [Description("The contact deactivated on date")]
        public DateTime DeactivatedOn { get; set; }
        [Description("The contact BusinessUnit")]
        public string BusinessUnit { get; set; }
        [Description("The contact related AccountGUID")]
        public Guid AccountGUID { get; set; }
        [Description("The contact Name")]
        public string Name { get; set; }
        [Description("The contact LanguageCode")]
        public string LanguageCode { get; set; }
        [Description("The contact Jobtitle")]
        public string Jobtitle { get; set; }
        [Description("The contact Email")]
        public string Email { get; set; }
        [Description("The contact Salutation")]
        public string Salutation { get; set; }
        [Description("The contact DirectPhone")]
        public string DirectPhone { get; set; }
        [Description("The contact MobilePhone")]
        public string MobilePhone { get; set; }
        [Description("The contact HomePhone")]
        public string HomePhone { get; set; }
        [Description("Is the contact Included in campaigns")]
        public bool IncludeInCampaigns { get; set; }
        [Description("The contact WebuserNumber")]
        public bool Webuser { get; set; }
        [Description("The id from Navision")]
        public string NavID { get; set; }

        [Description("Last modified By")]
        public string LastmodifiedBy { get; set; }

        [Description("Last modified At")]
        public DateTime LastmodifiedAt { get; set; }
        public override string GetSessionId() => ContactId.ToString();
    }
}
