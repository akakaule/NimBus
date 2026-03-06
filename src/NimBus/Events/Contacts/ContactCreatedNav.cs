using NimBus.Core.Events;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Events.Contacts
{
    [Description("Triggers whenever a contact is updated in Nav09")]
    public class ContactCreatedNav : Event
    {
        public static ContactCreatedNav Example = new ContactCreatedNav()
        {
            ContactId = Guid.NewGuid(),
            CreatedOn = DateTime.Now,
            BusinessUnit = "",
            AccountGUID = Guid.NewGuid(),
            Name = "Lars Larsen",
            LanguageCode = "AT",
            Jobtitle = "Salgs Chef",
            Email = "Lars@Larsen.eet.dk",
            Salutation = "Hr",
            DirectPhone = "12345678",
            MobilePhone = "12345678",
            HomePhone = "12345678",
            IncludeInCampaigns = true,
            Webuser = true,
            UserGroup = "userGroup1",
            PrimaryContact = true,
            LastLoginAt = DateTime.Now,
            SubscribeSource = "NAV",
            SubscribedAt = DateTime.Now,
            CreatedBy = "DIS",
            CreatedAt = DateTime.Now,
            LastModifiedBy = "DIS",
            LastModifiedAt = DateTime.Now,
            UnsubscribedAt = DateTime.Now,
            ContactHash = "Hash",
            ContactKey = "key"

        };

        [Required]
        [Description("The NAV09 contact record unique identifier")]
        public Guid ContactId { get; set; }
        [Description("The contact created on date")]
        public DateTime CreatedOn { get; set; }
        [Description("The contact description")]
        public string BusinessUnit { get; set; }
        [Required]
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
        [Description("The contact UserGroup")]
        public string UserGroup { get; set; }
        [Description("Is the contact PrimaryContact?")]
        public bool PrimaryContact { get; set; }
        [Description("The contact Last login timestamp")]
        public DateTime LastLoginAt { get; set; }
        [Description("The contact SubscribeSource")]
        public string SubscribeSource { get; set; }
        [Description("The contact SubscribedAt")]
        public DateTime SubscribedAt { get; set; }
        [Description("The contact CreatedBy")]
        public string CreatedBy { get; set; }
        [Description("The contact CreatedAt")]
        public DateTime CreatedAt { get; set; }
        [Description("The contact LastModifiedBy")]
        public string LastModifiedBy { get; set; }
        [Description("The contact LastModifiedAt")]
        public DateTime LastModifiedAt { get; set; }
        [Description("The contact UnsubscribedAt")]
        public DateTime UnsubscribedAt { get; set; }
        [Description("The contact contactKey")]
        public string ContactKey { get; set; }
        [Description("The contacthash")]
        public string ContactHash { get; set; }
        [Description("The id from Navision")]
        public string NavID { get; set; }

        public override string GetSessionId() => ContactId.ToString();
    }
}
