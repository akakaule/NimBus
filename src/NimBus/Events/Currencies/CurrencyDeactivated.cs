using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Text;

namespace NimBus.Events.Currencies
{
    [Description("Triggers whenever a currency is deactivated in NAV")]
    public class CurrencyDeactivated : Event
    {
        public static CurrencyDeactivated Example = new CurrencyDeactivated()
        {
            ExchangeRate = 1.000,
            CurrencyCode = "EUR"
        };

        [Required]
        [Description("ExchangeRate")]
        public double ExchangeRate { get; set; }

        [Required]
        [Description("CurrencyCode")]
        public string CurrencyCode { get; set; }
    }
}
