using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NimBus.Events.Currencies
{
    [Description("Triggers whenever a currency is created in NAV")]
    public class CurrencyCreated : Event
    {
        public static CurrencyCreated Example = new CurrencyCreated()
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
