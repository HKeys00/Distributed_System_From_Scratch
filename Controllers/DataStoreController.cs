using Distributed_System_From_Scratch.Services;
using Microsoft.AspNetCore.Mvc;

namespace Distributed_System_From_Scratch.Controllers
{
    [ApiController]
    [Route("/")]
    public class DataStoreController(IDataStoreService dataStoreService) : ControllerBase
    {
        #region Fields

        private readonly IDataStoreService _dataStoreService = dataStoreService;

        #endregion

        #region Methods

        [HttpGet("{id:int}")]
        public string Get([FromRoute] int id) => _dataStoreService.Get(id);

        #endregion
    }
}
