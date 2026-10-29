using Microsoft.AspNetCore.Mvc;

namespace WyckoffWebApi.Controllers;

[ApiController]
[Route("[controller]")]
public class PingController : ControllerBase
{
    public PingController()
    {
    }
    
    [HttpGet()]
    public Task<IActionResult> Get()
    {
        return Task.FromResult<IActionResult>(new OkObjectResult(""));
    }

    [HttpGet("Start")]
    public Task<IActionResult> Start()
    {
        //
        return Task.FromResult<IActionResult>(new OkObjectResult(""));
    }
}