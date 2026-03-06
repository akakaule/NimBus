using NimBus.Core.Events;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Events.Customers
{
    [Description("Triggers whenever a account is deactivated in CRM")]
    public class AccountDeactivated : Event
    {
        public static AccountDeactivated Example = new AccountDeactivated()
        {
            AccountId = Guid.NewGuid(),
            LastmodifiedAt = DateTime.Now,
            LastmodifiedBy = "CBR",
        };

        [Required]
        [Description("The CRM account record unique identifier")]
        public Guid AccountId { get; set; }

        [Description("Last modified By")]
        public string LastmodifiedBy { get; set; }

        [Description("Last modified At")]
        public DateTime LastmodifiedAt { get; set; }

        public override string GetSessionId() => AccountId.ToString();
    }
}
