using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.WebApp.Models;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Controllers
{
    public class DiagnosticsController : Controller
    {
        public IActionResult Index()
        {
            return View("EventGridViewer");
        }
    }
}
