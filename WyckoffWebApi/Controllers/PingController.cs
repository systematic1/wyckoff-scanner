using Microsoft.AspNetCore.Mvc;

namespace WyckoffWebApi.Controllers;

[ApiController]
[Route("[controller]")]
public class PingController : ControllerBase
{
    public PingController()
    {
    }
    
    // This endpoint is called from the self-hosted KeepAliveService running in this same app to
    //  keep IIS from shutting down the app pool when web requests have been idle for some time.
    [HttpGet()]
    public Task<IActionResult> Get()
    {
        return Task.FromResult<IActionResult>(new OkObjectResult(""));
    }

    [HttpGet("Start")]
    public Task<IActionResult> Start()
    {
        // To Do later - this will allow a web request to start the wyckoff scanner manually
        //  without having to wait on the time schedule trigger.
        return Task.FromResult<IActionResult>(new OkObjectResult(""));
    }
}