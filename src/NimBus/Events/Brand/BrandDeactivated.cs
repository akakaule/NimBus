using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Text;
using NimBus.Core.Events;

namespace NimBus.Events.Brand
{
    [Description("Triggers whenever a brand is deactivated in NAV")]
    public class BrandDeactivated : Event
    {
        public static BrandDeactivated Example = new BrandDeactivated()
        {
            Name = "EET",
            BrandNo = "123", 
            BrandType = "123",
        };

        [Required]
        [Description("The brand name")]
        public string Name { get; set; }

        [Required]
        [Description("The brandNo name")]
        public string BrandNo { get; set; }

        [Required]
        [Description("The BrandType name")]
        public string BrandType { get; set; }
    }
}
