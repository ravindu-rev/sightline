using Microsoft.AspNetCore.Mvc;
using Shop.Application.Products;

namespace Shop.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ProductsController(IProductService products) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ProductDto>> List(CancellationToken ct) => await products.ListAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductDto>> Get(int id, CancellationToken ct)
    {
        var product = await products.GetAsync(id, ct);
        return product is null ? NotFound() : product;
    }

    [HttpPost]
    public async Task<ActionResult<ProductDto>> Create(CreateProduct input, CancellationToken ct)
    {
        var created = await products.CreateAsync(input, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }
}
