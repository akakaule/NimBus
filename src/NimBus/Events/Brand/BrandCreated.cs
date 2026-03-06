using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Text;

namespace NimBus.Events.Brand
{
    [Description("Triggers whenever a brand is created in NAV")]
    public class BrandCreated : Event
    {
        public static BrandCreated Example = new BrandCreated()
        {
            Name = "EET",
            BrandNo = "123",
            BrandType = "Retail"
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
