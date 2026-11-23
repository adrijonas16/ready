-- Precios REFERENCIALES de mercado (Perú) para los productos pendientes.
-- Son precios de referencia vistos en tiendas (Tai Loy, Promart, Falabella, Plaza Vea),
-- NO el costo del dueño. Los productos siguen INACTIVOS: el admin revisa y ajusta su
-- margen y recién ahí los activa. Mientras tanto el cliente no los ve.
-- IMPORTANTE: solo rellena el precio si sigue en 0, así nunca pisa un precio que el
-- administrador ya haya ajustado a mano. Idempotente (seed_runs + guardia base_price = 0).

UPDATE products p SET base_price = v.precio
FROM (VALUES
  ('RD-P0001', 12.90), ('RD-P0002', 23.90), ('RD-P0003', 11.90), ('RD-P0004', 21.90),
  ('RD-P0005', 7.90),  ('RD-P0006', 13.90), ('RD-P0007', 9.90),  ('RD-P0008', 15.90),
  ('RD-P0009', 19.90), ('RD-P0010', 9.90),  ('RD-P0011', 4.50),  ('RD-P0012', 9.90),
  ('RD-P0013', 8.90),  ('RD-P0014', 1.00),  ('RD-P0015', 10.90), ('RD-P0016', 10.90),
  ('RD-P0017', 3.50),  ('RD-P0018', 1.20),  ('RD-P0019', 9.90),  ('RD-P0020', 1.50),
  ('RD-P0021', 2.50),  ('RD-P0022', 2.50),  ('RD-P0023', 6.90),  ('RD-P0024', 2.90),
  ('RD-P0025', 7.90),  ('RD-P0026', 2.50),  ('RD-P0027', 7.50),  ('RD-P0028', 12.90),
  ('RD-P0029', 14.90), ('RD-P0030', 5.50),  ('RD-P0031', 7.90),  ('RD-P0032', 16.90),
  ('RD-P0033', 12.90), ('RD-P0034', 9.90),  ('RD-P0035', 3.50),  ('RD-P0036', 3.70),
  ('RD-P0037', 6.90),  ('RD-P0038', 2.50),  ('RD-P0039', 3.50),  ('RD-P0040', 6.90),
  ('RD-P0041', 15.90), ('RD-P0042', 5.50),  ('RD-P0043', 5.50),  ('RD-P0044', 7.50),
  ('RD-P0045', 3.50),  ('RD-P0046', 4.90),  ('RD-P0047', 1.90),  ('RD-P0048', 7.90),
  ('RD-P0049', 3.90),  ('RD-P0050', 9.90),  ('RD-P0051', 2.90),  ('RD-P0052', 8.90),
  ('RD-P0053', 5.90),  ('RD-P0054', 6.90),  ('RD-P0055', 5.90),  ('RD-P0056', 6.90),
  ('RD-P0057', 4.50),  ('RD-P0058', 3.50),  ('RD-P0059', 14.90), ('RD-P0060', 24.90),
  ('RD-P0061', 12.90), ('RD-P0062', 16.90), ('RD-P0063', 9.90),  ('RD-P0064', 9.90),
  ('RD-P0065', 9.90),  ('RD-P0066', 14.90), ('RD-P0067', 6.90),  ('RD-P0068', 9.90),
  ('RD-P0069', 16.90), ('RD-P0070', 12.90), ('RD-P0071', 7.90),  ('RD-P0072', 8.90),
  ('RD-P0073', 49.90), ('RD-P0074', 35.90), ('RD-P0075', 4.90),  ('RD-P0076', 6.90),
  ('RD-P0077', 4.90),  ('RD-P0078', 9.90),  ('RD-P0079', 4.90),  ('RD-P0080', 4.90),
  ('RD-P0081', 5.90),  ('RD-P0082', 6.90),  ('RD-P0083', 12.90), ('RD-P0084', 5.50),
  ('RD-P0085', 5.50),  ('RD-P0086', 5.00),  ('RD-P0087', 7.50),  ('RD-P0088', 5.50),
  ('RD-P0089', 5.50),  ('RD-P0090', 5.20),  ('RD-P0091', 6.50),  ('RD-P0092', 7.90),
  ('RD-P0093', 7.90),  ('RD-P0094', 9.90),  ('RD-P0095', 5.60),  ('RD-P0096', 6.10),
  ('RD-P0097', 6.00),  ('RD-P0098', 5.50)
) AS v(sku, precio)
WHERE p.sku = v.sku AND p.base_price = 0;
