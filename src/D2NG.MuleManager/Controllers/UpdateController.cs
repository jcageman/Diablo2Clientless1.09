using D2NG.Core.D2GS.Items;
using D2NG.MuleManager.Services.MuleManager;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Controllers
{
    [ApiController]
    [Route("/items")]
    public class UpdateController : ControllerBase
    {

        private readonly IMuleManagerService _muleManagerService;
        private readonly IMuleManagerRepository _muleManagerRepository;

        public UpdateController(
            IMuleManagerService muleManagerService,
            IMuleManagerRepository muleManagerRepository)
        {
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
        public async Task<IActionResult> GetAllItems([FromQuery] QualityType? qualityType, [FromQuery] ItemName? itemName, [FromQuery] StatType[] statTypes, [FromQuery] ClassificationType? classification)
        {
            var items = await _muleManagerRepository.GetAllItems(qualityType, itemName, statTypes, classification);
            return Ok(items.MapToDto());
        }
    }
}
