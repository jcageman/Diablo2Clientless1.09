using D2NG.MuleManager.Services.MuleManager;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Controllers
{
    [ApiController]
    [Route("/items")]
    public class UpdateController : ControllerBase
    {

        private readonly ILogger<UpdateController> _logger;
        private readonly IMuleManagerService _muleManagerService;
        private readonly IMuleManagerRepository _muleManagerRepository;

        public UpdateController(ILogger<UpdateController> logger,
            IMuleManagerService muleManagerService,
            IMuleManagerRepository muleManagerRepository)
        {
            _logger = logger;
            _muleManagerService = muleManagerService;
            _muleManagerRepository = muleManagerRepository;
        }

        [HttpPut]
        public async Task<IActionResult> UpdateAll()
        {
            if(await _muleManagerService.UpdateAllAccounts())
            {
                return Ok();
            }

            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        [HttpGet]
        public async Task<IActionResult> GetAllItems()
        {
            return Ok(await _muleManagerRepository.GetAllItems());
        }
    }
}
