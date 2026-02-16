using Distributed_System_From_Scratch.Services;
using Microsoft.AspNetCore.Mvc;

namespace Distributed_System_From_Scratch.Controllers
{
    [ApiController]
    [Route("/")]
    public class DataStoreController(IDataStoreService dataStoreService) : ControllerBase
    {
        #region Methods

        [HttpGet("{id:int}")]
        public string? Get([FromRoute] int id) => dataStoreService.Get(id);

        [HttpPost("{id:int}/{value}")]
        public void Set([FromRoute] int id, [FromRoute] string value) => dataStoreService.Set(id, value);

        #endregion
    }
}
