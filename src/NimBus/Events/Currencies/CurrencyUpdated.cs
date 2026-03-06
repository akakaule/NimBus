using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Text;

namespace NimBus.Events.Currencies
{
    [Description("Triggers whenever a currency is updated in NAV")]
    public class CurrencyUpdated : Event
    {
        public static CurrencyUpdated Example = new CurrencyUpdated()
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
