using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Text;

namespace NimBus.Events.Customers
{
    [Description("Triggers whenever a account is deactivated in NAV")]
    public class ProspectDeactivated : Event
    {
        public static ProspectDeactivated Example = new ProspectDeactivated()
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
