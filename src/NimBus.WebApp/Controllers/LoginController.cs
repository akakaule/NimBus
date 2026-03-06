using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Controllers
{
    [Route("login")]
    public class LoginController : Controller
    {
        [Authorize]
        [Route("/login")]
        public IActionResult Index()
        {
            return base.Redirect("/");
        }
    }
}
