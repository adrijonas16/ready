using Microsoft.AspNetCore.Mvc;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Core.Entities;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly ProductRepository _productRepo;

    public ProductsController(ProductRepository productRepo)
    {
        _productRepo = productRepo;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<ProductResponse>>>> GetAll([FromQuery] string? category, [FromQuery] string? brand, [FromQuery] int limit = 100)
    {
        var products = await _productRepo.GetAll(category, brand, limit);
        
        return Ok(ApiResponse<IEnumerable<ProductResponse>>.Ok(products.Select(p => new ProductResponse
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            Category = p.Category,
            Brand = p.Brand,
            Sku = p.Sku,
            BasePrice = p.BasePrice,
            ImageUrl = p.ImageUrl,
            Stock = p.Stock,
            Attributes = p.Attributes
        })));
    }

    [HttpGet("search")]
    public async Task<ActionResult<ApiResponse<IEnumerable<ProductResponse>>>> Search([FromQuery] string q, [FromQuery] string? category, [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(ApiResponse<IEnumerable<ProductResponse>>.Fail("EMPTY_QUERY", "Debe ingresar una busqueda"));

        var products = await _productRepo.Search(q, category, limit);
        
        return Ok(ApiResponse<IEnumerable<ProductResponse>>.Ok(products.Select(p => new ProductResponse
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            Category = p.Category,
            Brand = p.Brand,
            Sku = p.Sku,
            BasePrice = p.BasePrice,
            ImageUrl = p.ImageUrl,
            Stock = p.Stock,
            Attributes = p.Attributes
        })));
    }

    [HttpGet("categories")]
    public async Task<ActionResult<ApiResponse<IEnumerable<string>>>> GetCategories()
    {
        var categories = await _productRepo.GetCategories();
        return Ok(ApiResponse<IEnumerable<string>>.Ok(categories));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<ProductResponse>>> GetById(Guid id)
    {
        var product = await _productRepo.GetById(id);
        if (product == null)
            return NotFound(ApiResponse<ProductResponse>.Fail("NOT_FOUND", "Producto no encontrado"));

        return Ok(ApiResponse<ProductResponse>.Ok(new ProductResponse
        {
            Id = product.Id,
            Name = product.Name,
            Description = product.Description,
            Category = product.Category,
            Brand = product.Brand,
            Sku = product.Sku,
            BasePrice = product.BasePrice,
            ImageUrl = product.ImageUrl,
            Stock = product.Stock,
            Attributes = product.Attributes
        }));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<Guid>>> Create([FromBody] ProductRequest request)
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Brand = request.Brand,
            Sku = request.Sku,
            BasePrice = request.BasePrice,
            ImageUrl = request.ImageUrl,
            Stock = request.Stock,
            Attributes = request.Attributes,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var id = await _productRepo.Create(product);
        return Ok(ApiResponse<Guid>.Ok(id));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<bool>>> Update(Guid id, [FromBody] ProductRequest request)
    {
        var product = await _productRepo.GetById(id);
        if (product == null)
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Producto no encontrado"));

        product.Name = request.Name;
        product.Description = request.Description;
        product.Category = request.Category;
        product.Brand = request.Brand;
        product.Sku = request.Sku;
        product.BasePrice = request.BasePrice;
        product.ImageUrl = request.ImageUrl;
        product.Stock = request.Stock;
        product.Attributes = request.Attributes;

        await _productRepo.Update(product);
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid id)
    {
        await _productRepo.Delete(id);
        return Ok(ApiResponse<bool>.Ok(true));
    }
}

public class ProductRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string Sku { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public string? ImageUrl { get; set; }
    public int Stock { get; set; }
    public string? Attributes { get; set; }
}