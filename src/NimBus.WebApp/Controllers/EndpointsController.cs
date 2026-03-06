using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.WebApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using NimBus.WebApp.Actions;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Controllers
{
    public class EndpointsController : Controller
    {
        private readonly IPlatform platform;
        private readonly IManagerClient managerClient;
        private readonly IConfiguration configuration;
        public EndpointsController(IPlatform platform, IManagerClient managerClient, IConfiguration configuration)
        {
            this.platform = platform;
            this.managerClient = managerClient;
            this.configuration = configuration;
        }

    }
}
