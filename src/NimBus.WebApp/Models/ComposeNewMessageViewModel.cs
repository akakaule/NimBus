using System.Collections.Generic;
using NimBus.Core.Events;

namespace NimBus.WebApp.Models
{
    public class ComposeNewMessageViewModel : EditMessageViewModel
    {
        public string EndpointId { get; set; }
        public IEnumerable<string> EventTypes { get; set; }
        public string SelectedEventType { get; set; }
        public Dictionary<string, IEvent> EventTemplates { get; set; }


        public const string DropDownElementId = "SelectedEventType";
        public const string MessageElementId = "EventMessageJsonTextArea";
        public const string TemplateDumpElementId = "EventTemplateDump";
    }
}
