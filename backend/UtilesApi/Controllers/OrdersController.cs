using System.Data;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Core.Entities;
using UtilesApi.Services;
using Microsoft.AspNetCore.RateLimiting;
using UtilesApi.Infrastructure.Security;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OrdersController : ControllerBase
{
    private const int MaxItems = 200;
    private const int MaxCantidad = 99;

    private readonly IDbConnectionFactory _db;
    private readonly ProductRepository _productRepo;
    private readonly UserRepository _userRepo;
    private readonly INotificationService _notifications;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(
        IDbConnectionFactory db,
        ProductRepository productRepo,
        UserRepository userRepo,
        INotificationService notifications,
        ILogger<OrdersController> logger)
    {
        _db = db;
        _productRepo = productRepo;
        _userRepo = userRepo;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>
    /// Crea el pedido del usuario logueado (el userId del cuerpo se ignora).
    /// El servidor pone los precios: precio de campaña o base + recargo de variante (el recargo que manda el cliente
    /// solo se acepta si no supera el recargo máximo de las variantes de ese producto). El envío sale de la configuración
    /// de la tienda (gratis desde envioGratisDesde). Descuenta stock (sin bajar de 0).
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimits.Orders)]
    [Honeypot]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> Create([FromBody] CreateOrderRequest request)
    {
        var userId = User.GetUserId();
        var user = userId.HasValue ? await _userRepo.GetById(userId.Value) : null;
        if (user == null)
            return Unauthorized(ApiResponse<OrderResponse>.Fail("UNAUTHORIZED", "Inicia sesión para comprar"));

        var error = ValidarPedido(request);
        if (error != null)
            return BadRequest(ApiResponse<OrderResponse>.Fail("INVALID_ORDER", error));

        var productIds = request.Items.Select(i => i.ProductId).Distinct().ToArray();
        var productos = (await _productRepo.GetByIds(productIds)).ToDictionary(p => p.Id);
        foreach (var pid in productIds)
        {
            if (!productos.TryGetValue(pid, out var p))
                return BadRequest(ApiResponse<OrderResponse>.Fail("PRODUCT_NOT_FOUND", $"Producto {pid} no encontrado"));
            if (!p.IsActive)
                return BadRequest(ApiResponse<OrderResponse>.Fail("PRODUCT_INACTIVE", $"El producto \"{p.Name}\" ya no está disponible"));
        }

        using var conn = _db.CreateConnection();
        conn.Open();

        // Recargo máximo permitido por producto: la suma del mayor recargo de cada tipo de variante
        var recargos = (await conn.QueryAsync<(Guid ProductId, decimal Maximo)>(@"
            SELECT product_id, SUM(maximo) FROM (
                SELECT t.product_id, t.id, GREATEST(MAX(COALESCE(v.price_modifier, 0)), 0) AS maximo
                FROM product_variant_types t
                JOIN product_variant_values v ON v.variant_type_id = t.id AND COALESCE(v.is_active, TRUE)
                WHERE t.product_id = ANY(@Ids)
                GROUP BY t.product_id, t.id
            ) x GROUP BY product_id", new { Ids = productIds }))
            .ToDictionary(r => r.ProductId, r => r.Maximo);

        var items = new List<OrderItem>();
        foreach (var i in request.Items)
        {
            var p = productos[i.ProductId];
            var precio = p.PrecioVigente;
            decimal recargo = 0;
            if (i.UnitPrice.HasValue)
            {
                var diferencia = Math.Round(i.UnitPrice.Value - precio, 2);
                var maximo = recargos.GetValueOrDefault(p.Id);
                if (diferencia > 0 && diferencia <= maximo + 0.005m) recargo = diferencia;
            }
            items.Add(new OrderItem
            {
                Id = Guid.NewGuid(),
                ProductId = p.Id,
                Quantity = i.Quantity,
                UnitPrice = Math.Round(precio + recargo, 2),
                Notes = Recortar(i.Notes, 1000),
                Para = Recortar(i.Para, 150)
            });
        }

        var subtotal = Math.Round(items.Sum(x => x.UnitPrice * x.Quantity), 2);
        var settings = await StoreSettings.Leer(conn);
        var costoEnvio = Math.Round(StoreSettings.CostoEnvio(settings, subtotal), 2);
        var total = subtotal + costoEnvio;

        // Listas de útiles del pedido (varias si son de varios hijos). Solo se guardan las que existen.
        var listasPedidas = (request.SupplyListIds ?? new List<Guid>())
            .Concat(request.SupplyListId.HasValue ? new[] { request.SupplyListId.Value } : Array.Empty<Guid>())
            .Where(x => x != Guid.Empty).Distinct().ToArray();
        var listas = listasPedidas.Length == 0 ? Array.Empty<Guid>()
            : (await conn.QueryAsync<Guid>("SELECT id FROM supply_lists WHERE id = ANY(@Ids)", new { Ids = listasPedidas }))
                .OrderBy(x => Array.IndexOf(listasPedidas, x)).ToArray();

        var pago = request.Pago == null ? null : new PagoDto
        {
            Metodo = Recortar(request.Pago.Metodo, 50),
            Operacion = Recortar(request.Pago.Operacion, 100),
            Estado = Recortar(request.Pago.Estado, 50)
        };
        var entrega = request.Entrega == null ? null : new EntregaDto
        {
            Fecha = Recortar(request.Entrega.Fecha, 30),
            Franja = Recortar(request.Entrega.Franja, 50)
        };
        var comprobante = request.Comprobante == null ? null : new ComprobanteDto
        {
            Tipo = request.Comprobante.Tipo.Trim().ToLowerInvariant(),
            Documento = Recortar(request.Comprobante.Documento, 20),
            RazonSocial = Recortar(request.Comprobante.RazonSocial, 200)
        };

        var orderId = Guid.NewGuid();
        var bajos = new List<(string Name, int Stock)>();
        using (var tx = conn.BeginTransaction())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO orders (id, user_id, supply_list_id, supply_list_ids, total, costo_envio, status, shipping_address, shipping_phone,
                                    contact_notes, tracking_number, pago, entrega, comprobante)
                VALUES (@Id, @UserId, @SupplyListId, @SupplyListIds, @Total, @CostoEnvio, @Status, @ShippingAddress, @ShippingPhone,
                        @ContactNotes, @TrackingNumber, @Pago::jsonb, @Entrega::jsonb, @Comprobante::jsonb)", new
            {
                Id = orderId,
                UserId = user.Id,
                SupplyListId = listas.Length > 0 ? listas[0] : (Guid?)null,
                SupplyListIds = listas.Length > 0 ? listas : null,
                Total = total,
                CostoEnvio = costoEnvio,
                Status = OrderStatus.RECIBIDO.ToString(),
                ShippingAddress = request.ShippingAddress.Trim(),
                ShippingPhone = request.ShippingPhone.Trim(),
                ContactNotes = Recortar(request.ContactNotes, 4000),
                TrackingNumber = $"TRK-{Guid.NewGuid().ToString()[..8].ToUpper()}",
                Pago = Validacion.ASerializar(pago),
                Entrega = Validacion.ASerializar(entrega),
                Comprobante = Validacion.ASerializar(comprobante)
            }, tx);

            foreach (var item in items)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO order_items (id, order_id, product_id, quantity, unit_price, notes, para)
                    VALUES (@Id, @OrderId, @ProductId, @Quantity, @UnitPrice, @Notes, @Para)",
                    new { item.Id, OrderId = orderId, item.ProductId, item.Quantity, item.UnitPrice, item.Notes, item.Para }, tx);
            }

            var nota = pago != null
                ? $"Pedido recibido. Pago {pago.Metodo ?? "-"} {pago.Estado ?? ""} (operación {pago.Operacion ?? "-"})".Trim()
                : "Pedido recibido";
            await AgregarHistorial(conn, tx, orderId, OrderStatus.RECIBIDO, nota, user.Id);

            var quedan = await AjustarStock(conn, tx, orderId, -1);
            bajos.AddRange(quedan.Where(q => q.Stock <= 5));
            tx.Commit();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _notifications.NotifyNewOrder(orderId.ToString(), user.Name, total);
                foreach (var (name, stock) in bajos)
                {
                    if (stock == 0) await _notifications.NotifyOutOfStock(name);
                    else await _notifications.NotifyLowStock(name, stock);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "No se pudo notificar el pedido {OrderId}", orderId); }
        });

        var response = (await BuildOrderResponses(conn, new[] { orderId })).First();
        return Ok(ApiResponse<OrderResponse>.Ok(response));
    }

    /// <summary>Un pedido: solo su dueño o ADMIN/OPERATOR.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> GetById(Guid id)
    {
        using var conn = _db.CreateConnection();
        var actual = await conn.QueryFirstOrDefaultAsync<EstadoPedido>("SELECT user_id, status FROM orders WHERE id = @Id", new { Id = id });
        if (actual == null)
            return NotFound(ApiResponse<OrderResponse>.Fail("NOT_FOUND", "Orden no encontrada"));
        if (!User.IsStaff() && actual.UserId != User.GetUserId())
            return StatusCode(403, ApiResponse<OrderResponse>.Fail("FORBIDDEN", "Este pedido no es tuyo"));

        var response = (await BuildOrderResponses(conn, new[] { id })).First();
        return Ok(ApiResponse<OrderResponse>.Ok(response));
    }

    /// <summary>Pedidos de un usuario: él mismo o ADMIN/OPERATOR.</summary>
    [HttpGet("user/{userId}")]
    public async Task<ActionResult<ApiResponse<IEnumerable<OrderResponse>>>> GetByUserId(Guid userId)
    {
        if (!User.IsStaff() && User.GetUserId() != userId)
            return StatusCode(403, ApiResponse<IEnumerable<OrderResponse>>.Fail("FORBIDDEN", "Solo puedes ver tus pedidos"));

        using var conn = _db.CreateConnection();
        var ids = (await conn.QueryAsync<Guid>(
            "SELECT id FROM orders WHERE user_id = @UserId ORDER BY created_at DESC", new { UserId = userId })).ToArray();
        return Ok(ApiResponse<IEnumerable<OrderResponse>>.Ok(await BuildOrderResponses(conn, ids)));
    }

    /// <summary>
    /// Cambia el estado (ADMIN/OPERATOR). Al pasar a CANCELADO se devuelve el stock; si se saca de CANCELADO se vuelve a descontar.
    /// </summary>
    [HttpPut("{id}/status")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateStatus(Guid id, [FromBody] UpdateOrderStatusRequest request)
    {
        if (!Enum.TryParse<OrderStatus>(request.Status, true, out var status) || !Enum.IsDefined(status))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_STATUS", "Estado inválido"));

        using var conn = _db.CreateConnection();
        conn.Open();
        Guid userId;
        using (var tx = conn.BeginTransaction())
        {
            var actual = await conn.QueryFirstOrDefaultAsync<EstadoPedido>(
                "SELECT user_id, status FROM orders WHERE id = @Id FOR UPDATE", new { Id = id }, tx);
            if (actual == null)
                return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Orden no encontrada"));
            userId = actual.UserId;

            var anterior = actual.Status;
            await conn.ExecuteAsync("UPDATE orders SET status = @Status, updated_at = NOW() WHERE id = @Id",
                new { Id = id, Status = status.ToString() }, tx);
            await AgregarHistorial(conn, tx, id, status, Recortar(request.Notes, 1000), User.GetUserId());

            if (status == OrderStatus.CANCELADO && anterior != OrderStatus.CANCELADO.ToString())
                await AjustarStock(conn, tx, id, +1);
            else if (status != OrderStatus.CANCELADO && anterior == OrderStatus.CANCELADO.ToString())
                await AjustarStock(conn, tx, id, -1);
            tx.Commit();
        }

        _ = Task.Run(async () =>
        {
            try { await _notifications.NotifyOrderStatusChange(userId.ToString(), id.ToString(), status.ToString()); }
            catch { /* aviso opcional */ }
        });

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>El dueño cancela su pedido mientras está RECIBIDO. Devuelve el stock.</summary>
    [HttpPut("{id}/cancel")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> Cancel(Guid id, [FromBody] CancelOrderRequest? request)
    {
        var userId = User.GetUserId();
        using var conn = _db.CreateConnection();
        conn.Open();
        using (var tx = conn.BeginTransaction())
        {
            var actual = await conn.QueryFirstOrDefaultAsync<EstadoPedido>(
                "SELECT user_id, status FROM orders WHERE id = @Id FOR UPDATE", new { Id = id }, tx);
            if (actual == null)
                return NotFound(ApiResponse<OrderResponse>.Fail("NOT_FOUND", "Orden no encontrada"));
            if (actual.UserId != userId)
                return StatusCode(403, ApiResponse<OrderResponse>.Fail("FORBIDDEN", "Solo quien hizo el pedido puede cancelarlo"));
            if (actual.Status != OrderStatus.RECIBIDO.ToString())
                return Conflict(ApiResponse<OrderResponse>.Fail("CANNOT_CANCEL",
                    "El pedido ya se está preparando y no se puede cancelar desde la web. Escríbenos por WhatsApp."));

            await conn.ExecuteAsync("UPDATE orders SET status = @Status, updated_at = NOW() WHERE id = @Id",
                new { Id = id, Status = OrderStatus.CANCELADO.ToString() }, tx);
            var motivo = Recortar(request?.Motivo, 500);
            await AgregarHistorial(conn, tx, id, OrderStatus.CANCELADO,
                motivo == null ? "Cancelado por el cliente" : $"Cancelado por el cliente: {motivo}", userId);
            await AjustarStock(conn, tx, id, +1);
            tx.Commit();
        }

        var response = (await BuildOrderResponses(conn, new[] { id })).First();
        return Ok(ApiResponse<OrderResponse>.Ok(response));
    }

    /// <summary>
    /// Todos los pedidos (ADMIN/OPERATOR). Filtros: status, q (código, cliente, correo, teléfono, dirección, notas;
    /// sin distinguir mayúsculas), from/to (fechas, to incluye todo el día). Paginado con page/pageSize;
    /// el total va en la cabecera X-Total-Count.
    /// </summary>
    [HttpGet]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<IEnumerable<OrderResponse>>>> GetAll(
        [FromQuery] string? status, [FromQuery] string? q, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var estados = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToUpperInvariant()).ToArray();
            where.Add("o.status = ANY(@Estados)");
            parameters.Add("Estados", estados);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            where.Add(@"(o.id::text ILIKE @Q OR o.tracking_number ILIKE @Q OR u.name ILIKE @Q OR u.email ILIKE @Q
                         OR o.shipping_phone ILIKE @Q OR o.shipping_address ILIKE @Q OR o.contact_notes ILIKE @Q)");
            parameters.Add("Q", $"%{q.Trim()}%");
        }
        var desde = Validacion.AUtcTexto(Validacion.LeerFecha(from));
        if (desde != null) { where.Add("o.created_at >= (@Desde)::timestamptz"); parameters.Add("Desde", desde); }
        var hasta = Validacion.AUtcTexto(Validacion.LeerFecha(to), finDeDia: true);
        if (hasta != null) { where.Add("o.created_at <= (@Hasta)::timestamptz"); parameters.Add("Hasta", hasta); }

        var filtro = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        parameters.Add("Limit", pageSize);
        parameters.Add("Offset", (page - 1) * pageSize);

        using var conn = _db.CreateConnection();
        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM orders o LEFT JOIN users u ON u.id = o.user_id {filtro}", parameters);
        var ids = (await conn.QueryAsync<Guid>(
            $"SELECT o.id FROM orders o LEFT JOIN users u ON u.id = o.user_id {filtro} ORDER BY o.created_at DESC LIMIT @Limit OFFSET @Offset",
            parameters)).ToArray();

        Response.Headers["X-Total-Count"] = total.ToString();
        return Ok(ApiResponse<IEnumerable<OrderResponse>>.Ok(await BuildOrderResponses(conn, ids)));
    }

    // Admin: get dashboard stats
    [HttpGet("stats")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<object>>> GetStats()
    {
        using var conn = _db.CreateConnection();

        var totalOrders = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM orders");
        var totalRevenue = await conn.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(total), 0) FROM orders WHERE status <> 'CANCELADO'");
        var pendingOrders = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM orders WHERE status = 'RECIBIDO'");
        var cancelledOrders = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM orders WHERE status = 'CANCELADO'");
        var totalProducts = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM products WHERE is_active = true AND category <> 'Servicios'");
        var lowStockCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM products WHERE is_active = true AND category <> 'Servicios' AND stock <= 5");
        var outOfStockCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM products WHERE is_active = true AND category <> 'Servicios' AND stock = 0");
        var totalLists = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM supply_lists");
        var pendingLists = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM supply_lists WHERE estado = 'PENDIENTE_REVISION'");
        var totalUsers = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users");

        var lowStockProducts = await conn.QueryAsync<dynamic>(
            @"SELECT id, name, stock, base_price as ""basePrice"" FROM products
              WHERE is_active = true AND category <> 'Servicios' AND stock <= 10 ORDER BY stock ASC LIMIT 20");

        return Ok(ApiResponse<object>.Ok(new
        {
            totalOrders,
            totalRevenue,
            pendingOrders,
            cancelledOrders,
            totalProducts,
            lowStockCount,
            outOfStockCount,
            totalLists,
            pendingLists,
            totalUsers,
            lowStockProducts
        }));
    }

    private static string? ValidarPedido(CreateOrderRequest r)
    {
        if (r.Items == null || r.Items.Count == 0) return "La orden debe tener al menos un producto";
        if (r.Items.Count > MaxItems) return $"La orden no puede tener más de {MaxItems} líneas";
        if (r.Items.Any(i => i.Quantity < 1 || i.Quantity > MaxCantidad)) return $"Cada cantidad debe estar entre 1 y {MaxCantidad}";
        if (r.Items.Any(i => i.ProductId == Guid.Empty)) return "Hay un producto inválido en el pedido";
        if (r.Items.Any(i => i.UnitPrice is < 0)) return "El precio no puede ser negativo";
        if (r.Items.Any(i => !Validacion.Largo(i.Notes, 1000) || !Validacion.Largo(i.Para, 150)))
            return "Las notas de un producto son demasiado largas";
        if (string.IsNullOrWhiteSpace(r.ShippingAddress)) return "Escribe la dirección de entrega";
        if (r.ShippingAddress.Trim().Length < 5) return "Agrega más detalle a la dirección: calle, número y distrito";
        if (r.ShippingAddress.Length > 300) return "La dirección puede tener hasta 300 caracteres";
        if (!Validacion.SinControl(r.ShippingAddress)) return "La dirección tiene caracteres no permitidos";
        if (Validacion.CelularPeru(r.ShippingPhone) == null) return "Escribe un celular de 9 dígitos que empiece con 9 (puedes poner +51)";
        if (!Validacion.Largo(r.ContactNotes, 2000)) return "Las indicaciones pueden tener hasta 2000 caracteres";
        if ((r.SupplyListIds?.Count ?? 0) > 50) return "Demasiadas listas en un pedido";
        if (r.Comprobante != null)
        {
            var tipo = (r.Comprobante.Tipo ?? "").Trim().ToLowerInvariant();
            if (tipo != "boleta" && tipo != "factura") return "El comprobante debe ser boleta o factura";
            if (tipo == "factura")
            {
                if (!Validacion.RucPeruValido(r.Comprobante.Documento)) return "Para factura escribe un RUC de 11 dígitos que empiece con 10 o 20";
                var razon = r.Comprobante.RazonSocial?.Trim() ?? "";
                if (razon.Length < 3) return "Para factura escribe la razón social";
                if (razon.Length > 200) return "La razón social puede tener hasta 200 caracteres";
            }
            else if (!string.IsNullOrWhiteSpace(r.Comprobante.Documento) && !Validacion.DniValido(r.Comprobante.Documento))
                return "El DNI debe tener 8 dígitos";
        }
        return null;
    }

    private static string? Recortar(string? texto, int max)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var t = texto.Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static Task AgregarHistorial(IDbConnection conn, IDbTransaction tx, Guid orderId, OrderStatus status, string? notes, Guid? changedBy) =>
        conn.ExecuteAsync(@"
            INSERT INTO order_status_history (order_id, status, notes, changed_by)
            VALUES (@OrderId, @Status, @Notes, @ChangedBy)",
            new { OrderId = orderId, Status = status.ToString(), Notes = notes, ChangedBy = changedBy }, tx);

    /// <summary>Suma (signo +1) o resta (-1) al stock las cantidades del pedido. Nunca baja de 0. Devuelve el stock que queda.</summary>
    private static async Task<List<(string Name, int Stock)>> AjustarStock(IDbConnection conn, IDbTransaction tx, Guid orderId, int signo)
    {
        var filas = await conn.QueryAsync<(string Name, int Stock, string Category)>(@"
            UPDATE products p SET stock = GREATEST(COALESCE(p.stock, 0) + @Signo * x.q, 0), updated_at = NOW()
            FROM (SELECT product_id, SUM(quantity)::int AS q FROM order_items WHERE order_id = @Id GROUP BY product_id) x
            WHERE p.id = x.product_id
            RETURNING p.name, p.stock, p.category", new { Id = orderId, Signo = signo }, tx);
        return filas.Where(f => f.Category != "Servicios").Select(f => (f.Name, f.Stock)).ToList();
    }

    /// <summary>Arma las respuestas de varios pedidos con 3 consultas (pedidos+cliente, ítems+producto, historial).</summary>
    private static async Task<List<OrderResponse>> BuildOrderResponses(IDbConnection conn, Guid[] orderIds)
    {
        if (orderIds.Length == 0) return new List<OrderResponse>();

        var orders = (await conn.QueryAsync<OrderRow>(@"
            SELECT o.*, u.name AS user_name, u.email AS user_email
            FROM orders o LEFT JOIN users u ON u.id = o.user_id
            WHERE o.id = ANY(@Ids)", new { Ids = orderIds })).ToDictionary(o => o.Id);

        var items = (await conn.QueryAsync<OrderItemRow>(@"
            SELECT oi.id, oi.order_id, oi.product_id, oi.quantity, oi.unit_price, oi.notes, oi.para, oi.created_at,
                   p.name AS product_name, p.image_url AS product_image_url
            FROM order_items oi LEFT JOIN products p ON p.id = oi.product_id
            WHERE oi.order_id = ANY(@Ids)
            ORDER BY oi.created_at, oi.id", new { Ids = orderIds })).ToLookup(i => i.OrderId);

        var history = (await conn.QueryAsync<OrderStatusHistory>(@"
            SELECT * FROM order_status_history WHERE order_id = ANY(@Ids) ORDER BY created_at DESC",
            new { Ids = orderIds })).ToLookup(h => h.OrderId);

        var result = new List<OrderResponse>();
        foreach (var id in orderIds)
        {
            if (!orders.TryGetValue(id, out var o)) continue;
            var lineas = items[id].Select(i => new OrderItemResponse
            {
                Id = i.Id,
                ProductId = i.ProductId,
                ProductName = i.ProductName ?? "Producto desconocido",
                ImageUrl = i.ProductImageUrl,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                Notes = i.Notes,
                Para = i.Para
            }).ToList();

            var listas = (o.SupplyListIds ?? Array.Empty<Guid>()).ToList();
            if (listas.Count == 0 && o.SupplyListId.HasValue) listas.Add(o.SupplyListId.Value);

            result.Add(new OrderResponse
            {
                Id = o.Id,
                UserId = o.UserId,
                UserName = o.UserName,
                UserEmail = o.UserEmail,
                SupplyListId = o.SupplyListId,
                SupplyListIds = listas,
                Subtotal = Math.Round(lineas.Sum(l => l.UnitPrice * l.Quantity), 2),
                CostoEnvio = o.CostoEnvio,
                Total = o.Total,
                Status = o.Status.ToString(),
                ShippingAddress = o.ShippingAddress,
                ShippingPhone = o.ShippingPhone,
                ContactNotes = o.ContactNotes,
                TrackingNumber = o.TrackingNumber,
                Pago = Validacion.Leer<PagoDto>(o.Pago),
                Entrega = Validacion.Leer<EntregaDto>(o.Entrega),
                Comprobante = Validacion.Leer<ComprobanteDto>(o.Comprobante),
                CreatedAt = o.CreatedAt,
                Items = lineas,
                StatusHistory = history[id].Select(h => new OrderStatusHistoryResponse
                {
                    Status = h.Status,
                    Notes = h.Notes,
                    CreatedAt = h.CreatedAt
                }).ToList()
            });
        }
        return result;
    }

    public class OrderRow : Order
    {
        public string? UserName { get; set; }
        public string? UserEmail { get; set; }
    }

    public class OrderItemRow : OrderItem
    {
        public string? ProductName { get; set; }
        public string? ProductImageUrl { get; set; }
    }

    public class EstadoPedido
    {
        public Guid UserId { get; set; }
        public string Status { get; set; } = "";
    }
}

public class UpdateOrderStatusRequest
{
    public string Status { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class CancelOrderRequest
{
    public string? Motivo { get; set; }
}
