using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Orleans;

using WA.Domain.ActivationCodes.Command;
using WA.GrainInterface.Read;
using WA.GrainInterface.Write.ActivationCode;
using WA.Shared.Configuration;

namespace WA.API.Command.Controllers;

[Route("api/[controller]")]
[ApiController]
public class ActivationCodesController(
    IClusterClient client,
    IHttpContextAccessor contextAccessor,
    IOptionsMonitor<ApplicationOptions> appOptions) : BaseController(contextAccessor, appOptions)
{
    private readonly IClusterClient _client = client;

    [HttpPost("redeem-activation-code")]
    public async Task<IActionResult> RedeemActivationCode(string activationCode)
    {
        if (string.IsNullOrWhiteSpace(activationCode))
            return BadRequest("Activation code is required.");

        activationCode = activationCode.ToUpper();

        var activationCodeRequestCounter = _client.GetGrain<IActivationCodeFailedRequestCounter>(UserId);

        if (!await activationCodeRequestCounter.IsRequestAllowed())
            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many failed activation attempts, please try again later.");

        var activationCodeWriter = _client.GetGrain<IActivationCodeWrite>(activationCode);
        var outcome = await activationCodeWriter.Receive(new RedeemActivationCodeCommand());
        if (outcome.Failed)
        {
            var problem = outcome.Problems.FirstOrDefault();
            int statusCode = int.TryParse(problem?.Code, out statusCode) ? statusCode : StatusCodes.Status500InternalServerError;

            if (StatusCodes.Status404NotFound.Equals(statusCode) || StatusCodes.Status400BadRequest.Equals(statusCode))
                activationCodeRequestCounter.LogFailedRequest(activationCode);

            return StatusCode(statusCode, problem.Description);
        }
        return Ok(await _client.GetGrain<IActivationCodeRead>(activationCode).GetPlansDetails());
    }
}
