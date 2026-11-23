-- Productos PENDIENTES (98 productos: marcas y categoría Inicial que faltaban).
-- Se cargan INACTIVOS y con precio 0 ("Pendiente" en el panel): no se ven en la tienda
-- hasta que el administrador les ponga precio y los active. No tocan nada existente.
-- Lo corre DatabaseInitializer una sola vez (queda en seed_runs) y solo agrega códigos nuevos.

-- Categoría nueva para listas de inicial (juguetes didácticos, aseo, etc.)
INSERT INTO categories (name) VALUES ('Inicial') ON CONFLICT (name) DO NOTHING;

-- Marcas que faltaban (fuertes en el mercado peruano)
INSERT INTO brands (name) VALUES
  ('Staedtler'), ('Crayola'), ('Pelikan'), ('BIC'), ('Pentel'),
  ('Atlas'), ('David'), ('Continental'), ('Deli'),
  -- Cuadernos: marcas muy pedidas en Perú
  ('Report'), ('Loro'), ('Minerva'), ('College'), ('Alpha')
ON CONFLICT (name) DO NOTHING;

CREATE TEMP TABLE _pend (sku VARCHAR(100), nombre TEXT, categoria VARCHAR(100), sub VARCHAR(100),
                         marca VARCHAR(100), precio DECIMAL(10,2), imagen TEXT, stock INT,
                         gama VARCHAR(20), activo BOOLEAN) ON COMMIT DROP;

INSERT INTO _pend (sku, nombre, categoria, sub, marca) VALUES
  -- ===== Colores =====
  ('RD-P0001', 'Colores Staedtler Noris caja x12', 'Colores', 'Lápices de colores', 'Staedtler'),
  ('RD-P0002', 'Colores Staedtler Noris caja x24', 'Colores', 'Lápices de colores', 'Staedtler'),
  ('RD-P0003', 'Colores Crayola largos caja x12', 'Colores', 'Lápices de colores', 'Crayola'),
  ('RD-P0004', 'Colores Crayola largos caja x24', 'Colores', 'Lápices de colores', 'Crayola'),
  ('RD-P0005', 'Crayones Crayola caja x12', 'Colores', 'Crayolas', 'Crayola'),
  ('RD-P0006', 'Crayones Crayola caja x24', 'Colores', 'Crayolas', 'Crayola'),
  ('RD-P0007', 'Crayones jumbo Crayola caja x8', 'Colores', 'Crayolas', 'Crayola'),
  ('RD-P0008', 'Plumones Crayola lavables caja x12', 'Colores', 'Plumones', 'Crayola'),
  ('RD-P0009', 'Plumones Crayola Super Tips x20', 'Colores', 'Plumones', 'Crayola'),
  ('RD-P0010', 'Plumones Pelikan escolares x12', 'Colores', 'Plumones', 'Pelikan'),
  ('RD-P0011', 'Marcador permanente Staedtler Lumocolor', 'Colores', 'Marcadores', 'Staedtler'),
  ('RD-P0012', 'Resaltador BIC Brite Liner blíster x4', 'Colores', 'Resaltadores', 'BIC'),
  ('RD-P0013', 'Resaltador Pelikan pastel x4', 'Colores', 'Resaltadores', 'Pelikan'),
  -- ===== Escritura =====
  ('RD-P0014', 'Bolígrafo BIC Cristal azul unidad', 'Escritura', 'Bolígrafos', 'BIC'),
  ('RD-P0015', 'Bolígrafo BIC Cristal azul caja x12', 'Escritura', 'Bolígrafos', 'BIC'),
  ('RD-P0016', 'Bolígrafo BIC Cristal negro caja x12', 'Escritura', 'Bolígrafos', 'BIC'),
  ('RD-P0017', 'Lapicero Pentel BK77 azul', 'Escritura', 'Bolígrafos', 'Pentel'),
  ('RD-P0018', 'Lápiz BIC Evolution HB unidad', 'Escritura', 'Lápices', 'BIC'),
  ('RD-P0019', 'Lápiz Staedtler Noris HB caja x12', 'Escritura', 'Lápices', 'Staedtler'),
  ('RD-P0020', 'Lápiz grafito Pentel HB unidad', 'Escritura', 'Lápices', 'Pentel'),
  ('RD-P0021', 'Borrador Staedtler Mars Plastic', 'Escritura', 'Borradores', 'Staedtler'),
  ('RD-P0022', 'Borrador Pentel Hi-Polymer', 'Escritura', 'Borradores', 'Pentel'),
  ('RD-P0023', 'Portaminas Pentel 0.5mm', 'Escritura', 'Portaminas y minas', 'Pentel'),
  ('RD-P0024', 'Minas Pentel 0.5mm HB tubo', 'Escritura', 'Portaminas y minas', 'Pentel'),
  ('RD-P0025', 'Portaminas Staedtler 0.7mm', 'Escritura', 'Portaminas y minas', 'Staedtler'),
  ('RD-P0026', 'Tajador Staedtler metálico', 'Escritura', 'Tajadores', 'Staedtler'),
  -- ===== Arte =====
  ('RD-P0027', 'Témpera David estuche x7 colores', 'Arte', 'Témperas', 'David'),
  ('RD-P0028', 'Témpera Pelikan estuche x12', 'Arte', 'Témperas', 'Pelikan'),
  ('RD-P0029', 'Témpera Crayola lavable x6', 'Arte', 'Témperas', 'Crayola'),
  ('RD-P0030', 'Plastilina David caja x12', 'Arte', 'Plastilinas', 'David'),
  ('RD-P0031', 'Plastilina Pelikan x10 colores', 'Arte', 'Plastilinas', 'Pelikan'),
  ('RD-P0032', 'Masa para modelar Crayola Model Magic', 'Arte', 'Masas moldeables', 'Crayola'),
  ('RD-P0033', 'Acuarela Pelikan pastillas x12', 'Arte', 'Acuarelas y óleos', 'Pelikan'),
  ('RD-P0034', 'Acuarela Crayola x8 colores', 'Arte', 'Acuarelas y óleos', 'Crayola'),
  -- ===== Pegar =====
  ('RD-P0035', 'Goma en barra Pelikan 20g', 'Pegar', 'Gomas y siliconas', 'Pelikan'),
  ('RD-P0036', 'Goma líquida escolar David 250g', 'Pegar', 'Gomas y siliconas', 'David'),
  ('RD-P0037', 'Silicona líquida David 250ml', 'Pegar', 'Gomas y siliconas', 'David'),
  ('RD-P0038', 'Cinta masking Deli 1 pulgada', 'Pegar', 'Cintas adhesivas', 'Deli'),
  ('RD-P0039', 'Cinta de embalaje Deli transparente', 'Pegar', 'Cintas adhesivas', 'Deli'),
  -- ===== Papeles =====
  ('RD-P0040', 'Cartulina escolar Continental colores x10', 'Papeles', 'Cartulinas', 'Continental'),
  ('RD-P0041', 'Papel bond Continental A4 75g x500', 'Papeles', 'Papel bond y fotográfico', 'Continental'),
  -- ===== Cuadernos =====
  ('RD-P0042', 'Cuaderno Atlas A4 cuadriculado 100h', 'Cuadernos', 'Cuadernos', 'Atlas'),
  ('RD-P0043', 'Cuaderno Atlas A4 rayado 100h', 'Cuadernos', 'Cuadernos', 'Atlas'),
  ('RD-P0044', 'Cuaderno Atlas deluxe cosido 100h', 'Cuadernos', 'Cuadernos premium', 'Atlas'),
  -- ===== Útiles =====
  ('RD-P0045', 'Tijera escolar Deli punta roma', 'Útiles', 'Tijeras', 'Deli'),
  ('RD-P0046', 'Juego de reglas Deli x4', 'Útiles', 'Reglas y geometría', 'Deli'),
  ('RD-P0047', 'Regla Deli 30cm', 'Útiles', 'Reglas y geometría', 'Deli'),
  ('RD-P0048', 'Folder manila Continental oficio x10', 'Útiles', 'Folders', 'Continental'),
  ('RD-P0049', 'Corrector de cinta Deli', 'Útiles', 'Otros útiles', 'Deli'),
  ('RD-P0050', 'Engrapador Deli escolar', 'Útiles', 'Otros útiles', 'Deli'),
  ('RD-P0051', 'Clips Deli metálicos caja x100', 'Útiles', 'Otros útiles', 'Deli');

-- ===== Inicial (listas de niños pequeños): aseo, juguetes didácticos, etc. =====
INSERT INTO _pend (sku, nombre, categoria, sub, marca) VALUES
  ('RD-P0052', 'Toallitas húmedas x100', 'Inicial', 'Aseo', NULL),
  ('RD-P0053', 'Toallitas húmedas x50', 'Inicial', 'Aseo', NULL),
  ('RD-P0054', 'Papel toalla pack x2', 'Inicial', 'Aseo', NULL),
  ('RD-P0055', 'Jabón líquido para manos 250ml', 'Inicial', 'Aseo', NULL),
  ('RD-P0056', 'Alcohol en gel 250ml', 'Inicial', 'Aseo', NULL),
  ('RD-P0057', 'Caja de pañuelos faciales', 'Inicial', 'Aseo', NULL),
  ('RD-P0058', 'Servilletas pack x100', 'Inicial', 'Aseo', NULL),
  ('RD-P0059', 'Rompecabezas de encastre de madera', 'Inicial', 'Juguetes didácticos', NULL),
  ('RD-P0060', 'Bloques de construcción x50 piezas', 'Inicial', 'Juguetes didácticos', NULL),
  ('RD-P0061', 'Cuentas para ensartar de colores', 'Inicial', 'Juguetes didácticos', NULL),
  ('RD-P0062', 'Títere de tela', 'Inicial', 'Juguetes didácticos', NULL),
  ('RD-P0063', 'Dominó infantil de figuras', 'Inicial', 'Juguetes didácticos', NULL),
  ('RD-P0064', 'Juego de memoria de animales', 'Inicial', 'Juguetes didácticos', NULL),
  ('RD-P0065', 'Lotería de figuras', 'Inicial', 'Juguetes didácticos', NULL),
  ('RD-P0066', 'Ábaco de colores', 'Inicial', 'Juguetes didácticos', NULL),
  ('RD-P0067', 'Plastilina gruesa x6 (inicial)', 'Inicial', 'Modelado y pintura', 'David'),
  ('RD-P0068', 'Masa para modelar blanda x4', 'Inicial', 'Modelado y pintura', NULL),
  ('RD-P0069', 'Pintura para dedos Crayola x4', 'Inicial', 'Modelado y pintura', 'Crayola'),
  ('RD-P0070', 'Pintura para dedos David x6', 'Inicial', 'Modelado y pintura', 'David'),
  ('RD-P0071', 'Crayones jumbo triangulares x8', 'Inicial', 'Modelado y pintura', NULL),
  ('RD-P0072', 'Témpera lavable inicial x6', 'Inicial', 'Modelado y pintura', NULL),
  ('RD-P0073', 'Mochila pequeña inicial', 'Inicial', 'Loncheras y mochilas', NULL),
  ('RD-P0074', 'Lonchera térmica inicial', 'Inicial', 'Loncheras y mochilas', NULL),
  ('RD-P0075', 'Cartulina iris A4 x10 (inicial)', 'Inicial', 'Papeles y manualidades', NULL),
  ('RD-P0076', 'Papel crepé colores x10', 'Inicial', 'Papeles y manualidades', NULL),
  ('RD-P0077', 'Papel lustre A4 x10', 'Inicial', 'Papeles y manualidades', NULL),
  ('RD-P0078', 'Goma EVA / microporoso planchas x10', 'Inicial', 'Papeles y manualidades', NULL),
  ('RD-P0079', 'Palitos de helado de colores x100', 'Inicial', 'Papeles y manualidades', NULL),
  ('RD-P0080', 'Limpiapipas de colores x50', 'Inicial', 'Papeles y manualidades', NULL),
  ('RD-P0081', 'Pompones de colores x100', 'Inicial', 'Papeles y manualidades', NULL),
  ('RD-P0082', 'Set de escarcha / glitter x6', 'Inicial', 'Papeles y manualidades', NULL),
  ('RD-P0083', 'Foami con brillo planchas x10', 'Inicial', 'Papeles y manualidades', NULL);

-- ===== Cuadernos de marcas peruanas muy pedidas =====
INSERT INTO _pend (sku, nombre, categoria, sub, marca) VALUES
  ('RD-P0084', 'Cuaderno Report A4 cuadriculado 100h', 'Cuadernos', 'Cuadernos', 'Report'),
  ('RD-P0085', 'Cuaderno Report A4 rayado 100h', 'Cuadernos', 'Cuadernos', 'Report'),
  ('RD-P0086', 'Cuaderno Report triple renglón 80h', 'Cuadernos', 'Cuadernos', 'Report'),
  ('RD-P0087', 'Cuaderno Report deluxe cosido 100h', 'Cuadernos', 'Cuadernos premium', 'Report'),
  ('RD-P0088', 'Cuaderno Loro deluxe cuadriculado 100h', 'Cuadernos', 'Cuadernos', 'Loro'),
  ('RD-P0089', 'Cuaderno Loro deluxe rayado 100h', 'Cuadernos', 'Cuadernos', 'Loro'),
  ('RD-P0090', 'Cuaderno Loro triple renglón 84h', 'Cuadernos', 'Cuadernos', 'Loro'),
  ('RD-P0091', 'Block Loro arcoíris A4 40h', 'Cuadernos', 'Blocks', 'Loro'),
  ('RD-P0092', 'Cuaderno Minerva anillado cuadriculado 150h', 'Cuadernos', 'Cuadernos premium', 'Minerva'),
  ('RD-P0093', 'Cuaderno Minerva anillado rayado 150h', 'Cuadernos', 'Cuadernos premium', 'Minerva'),
  ('RD-P0094', 'Block Minerva bitácora A4 90h', 'Cuadernos', 'Blocks', 'Minerva'),
  ('RD-P0095', 'Cuaderno College cuadriculado 80h', 'Cuadernos', 'Cuadernos', 'College'),
  ('RD-P0096', 'Cuaderno College triple renglón deluxe 100h', 'Cuadernos', 'Cuadernos', 'College'),
  ('RD-P0097', 'Cuaderno Alpha cuadriculado 92h', 'Cuadernos', 'Cuadernos', 'Alpha'),
  ('RD-P0098', 'Block Alpha perforado A4 50h', 'Cuadernos', 'Blocks', 'Alpha');

-- Valores por defecto de "pendiente": sin precio, sin stock, inactivo.
UPDATE _pend SET precio = 0, imagen = NULL, stock = 0, gama = 'medio', activo = FALSE;

INSERT INTO products (name, category, subcategory, brand, sku, base_price, image_url, stock, tier, is_active)
SELECT nombre, categoria, sub, marca, sku, precio, imagen, stock, gama, activo
FROM _pend c
WHERE NOT EXISTS (SELECT 1 FROM products p WHERE p.sku = c.sku);
