#!/usr/bin/env python3
"""
Genera backend/UtilesApi/Infrastructure/Database/seed-productos-<nombre>.sql a partir de un Excel con el formato de
"Catalogo_Utiles_Escolares_Precios_2026.xlsx" (hoja "Catálogo con precio": una fila por SKU o presentación).

Uso:  python3 scripts/generar_seed_productos.py ruta/al/archivo.xlsx nombre-del-seed

Qué hace con cada fila:
- Nombre legible: sin entidades HTML (&#8211;), sin la marca repetida al final, en minúsculas con mayúscula inicial,
  tildes en palabras comunes y abreviaturas expandidas ("CJAX12" → "caja x12").
- Precio: el precio actual publicado. El Excel guarda los precios con formato de fecha; las tiendas Shopify
  (Tienda Útil y Ofimarket) además vienen divididos entre 100 (S/ 3.20 figura como 0.032).
- Categoría: una de las de la tienda (Cuadernos, Colores, Arte, Escritura, Útiles, Pegar, Papeles, Libros), según
  la familia de la fuente y, en familias genéricas, palabras del nombre.
- Marca normalizada ("FABER CASTELL" → "Faber-Castell"); "Sin marca identificada" queda sin marca.
- Gama (económico / medio / premium): por tercios de precio dentro de cada familia, para los planes de las listas.
- Stock: 50 si la fuente lo indica disponible, 0 si no (se ve "Agotado"). Precio 0 → producto inactivo.
- Código: RD-<código de la fuente>. Las presentaciones de un mismo producto (colores, tamaños) quedan como
  variantes con su propio precio, foto y stock.

DatabaseInitializer corre cada seed-productos-*.sql una sola vez (queda en seed_runs) y nunca pisa productos
existentes: solo agrega los códigos que no estén en la base.
"""
import datetime
import html
import os
import re
import sys
import unicodedata
from collections import OrderedDict, defaultdict

import openpyxl
from openpyxl.utils.datetime import to_excel

STOCK_DISPONIBLE = 50
SHOPIFY = {'Tienda Útil', 'Ofimarket'}

MARCAS = {
    'NAVARRETE': 'Navarrete', 'ARTESCO': 'Artesco', 'FABER CASTELL': 'Faber-Castell', 'LAYCONSA': 'Layconsa',
    'VINIFAN': 'Vinifan', 'MAPED': 'Maped', 'SHURTAPE': 'Shurtape', 'PEGAFAN': 'Pegafan', 'GRANDES LIBROS': 'Grandes Libros',
    'PILOT': 'Pilot', 'STABILO': 'Stabilo', 'VIKINGO': 'Vikingo', 'CREATIVO': 'Creativo', 'UHU': 'UHU', 'EVAFLEX': 'Evaflex',
    'UNIBALL': 'Uni-ball', 'ESCUELA ACTIVA': 'Escuela Activa', 'SMART': 'Smart', 'STANDFORD': 'Standford',
    'CREATIVO_DIDACTICOS': 'Creativo Didáctico', 'CREATIVO DIDACTICO': 'Creativo Didáctico', 'CANSON': 'Canson', 'KP': 'KP',
    'CBIT': 'CBIT', 'JUSTUS': 'Justus', 'PARKER': 'Parker',
}
SIN_MARCA = {'SIN MARCA IDENTIFICADA', 'CARTÓN CORRUGADO'}

# Familia de la fuente → categoría de la tienda
FAMILIAS = {
    'Cuadernos': ['CUADERNOS Y AGENDAS', 'CUADERNOS THEME BOOK', 'CUADERNOS EMPASTADOS', 'CUADERNOS PREMIUM', 'CUADERNOS D-LUXE',
                  'CUADERNOS DE DIBUJO', 'CUADERNOS DE MUSICA', 'CUADERNOS ESPIRALADOS', 'CUADERNOS DE CONTROL', 'BLOCKS ESCOLARES',
                  'BLOCKS Y HOJAS', 'BLOCKS DE DIBUJO', 'SKETCH BOOK Y BITACORAS'],
    'Colores': ['PLUMONES', 'COLORES', 'RESALTADOR', 'CRAYOLAS', 'LAPIZ DE COLOR', 'COLORES, CRAYONES Y PLASTILINA',
                'PLUMONES Y RESALTADORES', 'MARCADORES', 'ROTULADORES', 'COLORES TÉCNICOS'],
    'Arte': ['PINTURA ACRILICA', 'TEMPERAS SUELTAS', 'TEMPERAS EN ESTUCHES', 'TEMPERAS EN BARRAS', 'ACUARELAS Y OLEOS',
             'ESTUCHE ACUARELAS Y OLEOS', 'PINCELES Y PALETAS', 'ACCESORIOS DE ARTE', 'PITT GRAPHITE MATT', 'PITT PASTEL',
             'PITT MONOCHROME', 'PITT ARTIST PEN', 'LIENZOS Y PINCELES', 'MASAS MOLDEABLES', 'PLASTILINAS', 'MANUALIDADES',
             'TIZA PASTEL', 'ALBRECHT DURER', 'POLYCHROMOS', 'GRAFITO ACUARELABLE', 'MARCADORES Y LIBROS PARA LETTERING',
             'CUERO SPUM', 'EVA FOAM', 'EVA FOAM GLITTER'],
    'Escritura': ['BOLIGRAFOS TINTA GEL', 'BOLIGRAFOS TINTA SECA', 'BOLIGRAFOS BORRABLES', 'BOLIGRAFOS INVISIBLES',
                  'LAPICES ESCOLARES', 'LAPICES TECNICOS', 'PORTAMINAS Y MINAS', 'PORTAMINAS', 'CASTELL 9000',
                  'LÁPICES Y PORTAMINAS', 'CORRECTORES', 'BORRADORES', 'TAJADORES', 'BORRADORES, TAJADORES Y CORRECTORES',
                  'LIMPIATIPOS', 'TIZAS'],
    'Pegar': ['CINTAS ADHESIVAS', 'PEGAMENTOS Y SILICONAS', 'ETIQUETAS AUTOADHESIVAS', 'ETIQUETAS AUTOADHESIVAS CON DISEÑO'],
    'Papeles': ['PAPEL DE REGALO', 'CARTULINA PLASTIFICADA', 'PAPEL CREPE', 'PAPEL SEDA', 'PAPEL LUSTRE', 'PAPEL ARCOIRIS',
                'CARTÓN CORRUGADO', 'CARTULINA METALIZADA', 'PAPEL FOTOGRÁFICO', 'CARTULINA ESCOLAR', 'PAPEL BOND', 'PAPEL OROPEL',
                'CARTULINA FOSFORECENTE', 'CARTULINA HOLOGRAFICA', 'PAPELOGRAFO', 'CARTULINA DUPLEX', 'PAPEL CELOFAN', 'CARTULINA',
                'PAPELES Y CARTULINAS', 'FICHAS BIBLIOGRAFICAS', 'PAPEL KRAFT', 'BLOCKS DE MANUALIDADES'],
    'Libros': ['INGLES', 'MATEMÁTICAS', 'COMUNICACIÓN', 'PLAN LECTOR', 'CIENCIA Y TECNOLOGÍA', 'LIBROS Y DICCIONARIOS'],
    'Útiles': ['TIJERAS', 'REGLAS Y ESCUADRAS', 'COMPASES', 'GEOMETRÍA Y CORTE', 'FOLDER PLASTICO COLOR ENTERO',
               'FOLDER PLASTICO CON DISEÑOS', 'FOLDER PLASTICO CON LICENCIA', 'FOLDERS Y ORGANIZACIÓN', 'FOLDERS CON LIGA',
               'PIZARRAS Y MOTAS', 'MISCELANEA', 'TABLA DE PUNZADO', 'KAWAII', 'FORROS'],
}
CATEGORIA_DE_FAMILIA = {f: c for c, fs in FAMILIAS.items() for f in fs}
# Familias genéricas de la fuente: la categoría se decide por el nombre
GENERICAS = {'ÚTILES ESCOLARES', 'OTROS ÚTILES ESCOLARES', 'ADHESIVOS Y CORRECCIÓN'}
POR_PALABRA = [  # (categoría, palabras al comienzo de alguna palabra del nombre); gana la primera que coincide
    ('Libros', ['diccionario', 'libro']),
    ('Cuadernos', ['cuaderno', 'block', 'agenda']),
    ('Colores', ['colores', 'plumon', 'plumón', 'resaltador', 'crayola', 'crayón', 'crayon', 'marcador']),
    ('Arte', ['plastilina', 'tempera', 'témpera', 'acuarela', 'pincel', 'masa', 'témperas', 'pintura', 'óleo', 'oleo']),
    ('Escritura', ['lápiz', 'lapiz', 'lápices', 'lapices', 'bolígrafo', 'boligrafo', 'lapicero', 'borrador', 'tajador',
                   'corrector', 'portaminas', 'tiza']),
    ('Pegar', ['cola', 'goma', 'silicona', 'pegamento', 'cinta', 'etiqueta']),
    ('Papeles', ['papel', 'cartulina', 'hojas']),
]

# Familia de la fuente → subcategoría (dentro de su categoría)
SUB_DE_FAMILIA = {
    'CUADERNOS Y AGENDAS': 'Cuadernos', 'CUADERNOS THEME BOOK': 'Cuadernos', 'CUADERNOS EMPASTADOS': 'Cuadernos',
    'CUADERNOS ESPIRALADOS': 'Cuadernos', 'CUADERNOS DE CONTROL': 'Cuadernos', 'CUADERNOS PREMIUM': 'Cuadernos premium',
    'CUADERNOS D-LUXE': 'Cuadernos premium', 'CUADERNOS DE DIBUJO': 'Cuadernos de dibujo y música',
    'CUADERNOS DE MUSICA': 'Cuadernos de dibujo y música', 'BLOCKS ESCOLARES': 'Blocks', 'BLOCKS Y HOJAS': 'Blocks',
    'BLOCKS DE DIBUJO': 'Blocks de dibujo', 'SKETCH BOOK Y BITACORAS': 'Sketchbooks y bitácoras',
    'COLORES': 'Lápices de colores', 'LAPIZ DE COLOR': 'Lápices de colores', 'PLUMONES': 'Plumones',
    'RESALTADOR': 'Resaltadores', 'CRAYOLAS': 'Crayolas', 'MARCADORES': 'Marcadores', 'ROTULADORES': 'Marcadores',
    'COLORES TÉCNICOS': 'Colores profesionales',
    'PINTURA ACRILICA': 'Pintura acrílica', 'TEMPERAS SUELTAS': 'Témperas', 'TEMPERAS EN ESTUCHES': 'Témperas',
    'TEMPERAS EN BARRAS': 'Témperas', 'ACUARELAS Y OLEOS': 'Acuarelas y óleos', 'ESTUCHE ACUARELAS Y OLEOS': 'Acuarelas y óleos',
    'PINCELES Y PALETAS': 'Pinceles, paletas y lienzos', 'LIENZOS Y PINCELES': 'Pinceles, paletas y lienzos',
    'ACCESORIOS DE ARTE': 'Accesorios de arte', 'PITT GRAPHITE MATT': 'Arte profesional', 'PITT PASTEL': 'Arte profesional',
    'PITT MONOCHROME': 'Arte profesional', 'PITT ARTIST PEN': 'Arte profesional', 'POLYCHROMOS': 'Arte profesional',
    'ALBRECHT DURER': 'Arte profesional', 'GRAFITO ACUARELABLE': 'Arte profesional', 'TIZA PASTEL': 'Arte profesional',
    'MARCADORES Y LIBROS PARA LETTERING': 'Lettering', 'MASAS MOLDEABLES': 'Masas moldeables', 'PLASTILINAS': 'Plastilinas',
    'MANUALIDADES': 'Manualidades', 'CUERO SPUM': 'Goma EVA y microporoso', 'EVA FOAM': 'Goma EVA y microporoso',
    'EVA FOAM GLITTER': 'Goma EVA y microporoso',
    'BOLIGRAFOS TINTA GEL': 'Bolígrafos', 'BOLIGRAFOS TINTA SECA': 'Bolígrafos', 'BOLIGRAFOS BORRABLES': 'Bolígrafos',
    'BOLIGRAFOS INVISIBLES': 'Bolígrafos', 'LAPICES ESCOLARES': 'Lápices', 'CASTELL 9000': 'Lápices', 'LAPICES TECNICOS': 'Lápices',
    'PORTAMINAS Y MINAS': 'Portaminas y minas', 'PORTAMINAS': 'Portaminas y minas', 'CORRECTORES': 'Correctores',
    'BORRADORES': 'Borradores', 'LIMPIATIPOS': 'Borradores', 'TAJADORES': 'Tajadores', 'TIZAS': 'Tizas',
    'CINTAS ADHESIVAS': 'Cintas adhesivas', 'PEGAMENTOS Y SILICONAS': 'Gomas y siliconas',
    'ETIQUETAS AUTOADHESIVAS': 'Etiquetas y stickers', 'ETIQUETAS AUTOADHESIVAS CON DISEÑO': 'Etiquetas y stickers',
    'PAPEL DE REGALO': 'Papel de regalo', 'CARTULINA PLASTIFICADA': 'Cartulinas', 'CARTULINA METALIZADA': 'Cartulinas',
    'CARTULINA ESCOLAR': 'Cartulinas', 'CARTULINA FOSFORECENTE': 'Cartulinas', 'CARTULINA HOLOGRAFICA': 'Cartulinas',
    'CARTULINA DUPLEX': 'Cartulinas', 'CARTULINA': 'Cartulinas', 'PAPEL CREPE': 'Papel crepé', 'PAPEL SEDA': 'Papel seda',
    'PAPEL LUSTRE': 'Papel lustre', 'PAPEL FOTOGRÁFICO': 'Papel bond y fotográfico', 'PAPEL BOND': 'Papel bond y fotográfico',
    'BLOCKS DE MANUALIDADES': 'Blocks de manualidades', 'FICHAS BIBLIOGRAFICAS': 'Otros papeles',
    'PAPEL ARCOIRIS': 'Otros papeles', 'PAPEL OROPEL': 'Otros papeles', 'PAPEL CELOFAN': 'Otros papeles',
    'PAPEL KRAFT': 'Otros papeles', 'CARTÓN CORRUGADO': 'Otros papeles', 'PAPELOGRAFO': 'Otros papeles',
    'INGLES': 'Inglés', 'MATEMÁTICAS': 'Matemática', 'COMUNICACIÓN': 'Comunicación', 'PLAN LECTOR': 'Plan lector',
    'CIENCIA Y TECNOLOGÍA': 'Ciencia y Tecnología',
    'TIJERAS': 'Tijeras', 'REGLAS Y ESCUADRAS': 'Reglas y geometría', 'COMPASES': 'Reglas y geometría',
    'GEOMETRÍA Y CORTE': 'Reglas y geometría', 'FOLDER PLASTICO COLOR ENTERO': 'Folders', 'FOLDER PLASTICO CON DISEÑOS': 'Folders',
    'FOLDER PLASTICO CON LICENCIA': 'Folders', 'FOLDERS Y ORGANIZACIÓN': 'Folders', 'FOLDERS CON LIGA': 'Folders',
    'FORROS': 'Forros', 'PIZARRAS Y MOTAS': 'Pizarras', 'MISCELANEA': 'Otros útiles', 'KAWAII': 'Otros útiles',
    'TABLA DE PUNZADO': 'Otros útiles',
}
# Familias genéricas: subcategoría por palabras del nombre, dentro de la categoría ya elegida
SUB_POR_PALABRA = {
    'Cuadernos': [('Blocks', ['block']), ('Cuadernos', [''])],
    'Colores': [('Plumones', ['plumon']), ('Resaltadores', ['resaltador']), ('Crayolas', ['crayola', 'crayon']),
                ('Marcadores', ['marcador']), ('Lápices de colores', [''])],
    'Arte': [('Plastilinas', ['plastilina']), ('Témperas', ['tempera']), ('Acuarelas y óleos', ['acuarela', 'oleo']),
             ('Pinceles, paletas y lienzos', ['pincel', 'paleta', 'lienzo']), ('Masas moldeables', ['masa', 'arcilla']),
             ('Goma EVA y microporoso', ['eva', 'microporoso']), ('Manualidades', [''])],
    'Escritura': [('Borradores', ['borrador']), ('Tajadores', ['tajador']), ('Correctores', ['corrector']),
                  ('Portaminas y minas', ['portaminas', 'minas']), ('Bolígrafos', ['boligrafo', 'lapicero']),
                  ('Tizas', ['tiza']), ('Lápices', [''])],
    'Pegar': [('Cintas adhesivas', ['cinta']), ('Etiquetas y stickers', ['etiqueta', 'sticker']), ('Gomas y siliconas', [''])],
    'Papeles': [('Cartulinas', ['cartulina']), ('Papel crepé', ['crepe']), ('Papel seda', ['seda']), ('Papel lustre', ['lustre']),
                ('Papel de regalo', ['regalo']), ('Papel bond y fotográfico', ['bond', 'fotografico']), ('Otros papeles', [''])],
    'Libros': [('Diccionarios', ['diccionario']), ('Otros libros', [''])],
    'Útiles': [('Tijeras', ['tijera']), ('Reglas y geometría', ['regla', 'escuadra', 'transportador', 'compas']),
               ('Folders', ['folder', 'archivador', 'sobre']), ('Forros', ['forro']), ('Pizarras', ['pizarra']), ('Otros útiles', [''])],
}

TILDES = {
    'oleo': 'óleo', 'lapiz': 'lápiz', 'lapices': 'lápices', 'boligrafo': 'bolígrafo', 'boligrafos': 'bolígrafos',
    'plastico': 'plástico', 'plastica': 'plástica', 'circulos': 'círculos', 'papelografo': 'papelógrafo', 'matematica': 'matemática',
    'matematicas': 'matemáticas', 'tempera': 'témpera', 'temperas': 'témperas', 'acrilica': 'acrílica', 'acrilico': 'acrílico',
    'metalico': 'metálico', 'metalica': 'metálica', 'metalizada': 'metalizada', 'tecnico': 'técnico', 'tecnicos': 'técnicos',
    'musica': 'música', 'correccion': 'corrección', 'liquida': 'líquida', 'liquido': 'líquido', 'magico': 'mágico',
    'magica': 'mágica', 'ingles': 'inglés', 'comunicacion': 'comunicación', 'numero': 'número', 'carton': 'cartón',
    'blister': 'blíster', 'arcoiris': 'arcoíris', 'celofan': 'celofán', 'lamina': 'lámina', 'laminas': 'láminas',
    'didactico': 'didáctico', 'didacticos': 'didácticos', 'practico': 'práctico', 'grafico': 'gráfico', 'fotografico': 'fotográfico',
    'holografica': 'holográfica', 'holografico': 'holográfico', 'fosforescente': 'fosforescente', 'electrico': 'eléctrico',
    'cuadricula': 'cuadrícula', 'genero': 'género', 'clasico': 'clásico', 'clasicos': 'clásicos', 'clasica': 'clásica',
    'periodico': 'periódico', 'bibliograficas': 'bibliográficas', 'jumbo': 'jumbo', 'ecologico': 'ecológico',
    'tecnologia': 'tecnología', 'pequeño': 'pequeño', 'estandar': 'estándar', 'regla': 'regla', 'escuadras': 'escuadras',
    'escolares': 'escolares', 'neon': 'neón', 'plumon': 'plumón', 'plumones': 'plumones', 'marron': 'marrón',
    'bambu': 'bambú', 'basicos': 'básicos', 'pizarron': 'pizarrón', 'cuadriculado': 'cuadriculado', 'electronico': 'electrónico',
    'lapicero': 'lapicero', 'imantada': 'imantada', 'organico': 'orgánico', 'pequeno': 'pequeño', 'tamano': 'tamaño',
    'acrilicas': 'acrílicas', 'metalicos': 'metálicos', 'metalicas': 'metálicas', 'plasticos': 'plásticos', 'plasticas': 'plásticas', 'triangulo': 'triángulo', 'unico': 'único', 'basico': 'básico', 'basica': 'básica',
}
ABREVIATURAS = [  # (patrón sobre el texto en minúsculas, reemplazo)
    (r'\bcjax\s*(\d)', r'caja x\1'), (r'\bcja\.?\s*x\s*(\d)', r'caja x\1'), (r'\bcja\b\.?', 'caja'),
    (r'\bbltx\s*(\d)', r'blíster x\1'), (r'\bblt\.?\s*x\s*(\d)', r'blíster x\1'), (r'\bblt\b\.?', 'blíster'),
    (r'\bbolx\s*(\d)', r'bolsa x\1'), (r'\bbol\.?\s*x\s*(\d)', r'bolsa x\1'),
    (r'\bbolsax\s*(\d)', r'bolsa x\1'), (r'\bblsx\s*(\d)', r'bolsa x\1'), (r'\bbls\.?\s*x\s*(\d)', r'bolsa x\1'),
    (r'\bx\s*(\d+)\s*und\b\.?', r'x\1 unid.'), (r'\b(\w+)x(\d+)\b', lambda m: m.group(0) if m.group(1)[-1].isdigit() else f'{m.group(1)} x{m.group(2)}'),
    (r'\bpizarr?\.', 'pizarra'), (r'\best\.\s', 'estuche '), (r'\best\s+x\s*(\d)', r'estuche x\1'),
    (r'\bestx\s*(\d)', r'estuche x\1'), (r'\bblisterx\s*(\d)', r'blíster x\1'),
    (r'\bbolig\.', 'bolígrafo '), (r'\betiq\.', 'etiquetas '), (r'\bunid\b\.?', 'unid.'), (r'\bund\b\.?', 'unid.'),
    (r'\bx(\d+)unid\b\.?', r'x\1 unid.'), (r'\s+', ' '),
]
MAYUSCULAS = re.compile(r'^([a-wyz]\d{1,2}|\d?b|hb|\d+b|pvc|eva|ii|iii|iv|uhu|kp|cbit|usb|led|3d|a4|a3|a5)$')
PALABRAS_MENORES = {'de', 'del', 'la', 'el', 'y', 'con', 'para', 'en', 'x', 'a', 'por', 'sin', 'o', 'e', 'al', 'los', 'las'}


def precio(valor, fuente: str):
    """El Excel trae los precios con formato de fecha u hora; se vuelven a número."""
    if valor is None:
        return None
    if isinstance(valor, datetime.time):
        n = (valor.hour * 3600 + valor.minute * 60 + valor.second + valor.microsecond / 1e6) / 86400
    elif isinstance(valor, datetime.datetime):
        n = float(to_excel(valor))
    else:
        n = float(valor)
    if fuente in SHOPIFY:
        n *= 100
    return round(n, 2)


def sin_tildes(t: str) -> str:
    return ''.join(c for c in unicodedata.normalize('NFD', t) if unicodedata.category(c) != 'Mn')


def marca_de(texto: str):
    t = (texto or '').strip().upper()
    if not t or t in SIN_MARCA:
        return None
    return MARCAS.get(t) or MARCAS.get(sin_tildes(t)) or texto.strip().title()


def nombre_legible(crudo: str, marca) -> str:
    t = html.unescape(crudo).replace(' ', ' ').strip()
    t = re.sub(r'\s+', ' ', t)
    # La marca repetida al final: "… – ARTESCO", "… - LAYCONSA"
    if marca:
        t = re.sub(rf'\s*[–\-]\s*{re.escape(marca)}\s*$', '', t, flags=re.I)
        t = re.sub(rf'\s*[–\-]\s*{re.escape(sin_tildes(marca))}\s*$', '', t, flags=re.I)
    # Otra etiqueta corta tras una raya al final ("… – arti")
    t = re.sub(r'\s+–\s+[\w ]{2,15}$', '', t)
    t = t.strip(' -–')
    mixto = any(c.islower() for c in t)
    if not mixto:  # todo en mayúsculas: se pasa a minúsculas con mayúscula inicial
        t = t.lower()
        for patron, reemplazo in ABREVIATURAS:
            t = re.sub(patron, reemplazo, t)
        palabras = []
        for p in t.strip().split(' '):
            base = p.strip('.,()/')
            if MAYUSCULAS.match(base):
                p = p.replace(base, base.upper())
            elif base in TILDES:
                p = p.replace(base, TILDES[base])
            palabras.append(p)
        t = ' '.join(palabras)
        t = t[:1].upper() + t[1:]
    # Las marcas conocidas con su escritura
    for m in set(MARCAS.values()):
        t = re.sub(rf'\b{re.escape(m)}\b', m, t, flags=re.I)
    t = re.sub(r'\s+', ' ', t).strip()
    if t.count('(') > t.count(')'):
        t += ')'
    return t


def categoria(familia: str, nombre: str) -> str:
    f = (familia or '').strip().upper()
    if f in CATEGORIA_DE_FAMILIA and f not in GENERICAS:
        return CATEGORIA_DE_FAMILIA[f]
    palabras = sin_tildes(nombre.lower()).split()
    for cat, claves in POR_PALABRA:
        if any(any(p.startswith(sin_tildes(k)) for p in palabras) for k in claves):
            return cat
    return 'Útiles'


def subcategoria(cat: str, familia: str, nombre: str) -> str:
    f = (familia or '').strip().upper()
    if f in SUB_DE_FAMILIA and f not in GENERICAS:
        return SUB_DE_FAMILIA[f]
    palabras = sin_tildes(nombre.lower()).split()
    for sub, claves in SUB_POR_PALABRA[cat]:
        if any(k == '' or any(p.startswith(k) for p in palabras) for k in claves):
            return sub
    return 'Otros útiles'


def slug(texto: str) -> str:
    return re.sub(r'[^a-z0-9]+', '-', sin_tildes(texto.lower())).strip('-')


def sql(v):
    if v is None:
        return 'NULL'
    if isinstance(v, (int, float)):
        return repr(v)
    return "'" + str(v).replace("'", "''") + "'"


COLORES = {'rojo', 'azul', 'amarillo', 'verde', 'blanco', 'naranja', 'rosado', 'celeste', 'negro', 'marrón', 'azulino',
           'verde limón', 'fucsia', 'plomo', 'morado', 'lila', 'dorado', 'plateado', 'turquesa', 'violeta', 'gris'}


def main(ruta: str, nombre_seed: str):
    wb = openpyxl.load_workbook(ruta, data_only=True)
    filas = [r for r in wb['Catálogo con precio'].iter_rows(min_row=6, values_only=True) if r[3] and r[4]]

    # Un producto por (fuente, nombre publicado) en las tiendas con variantes; en Navarrete, uno por código
    grupos: 'OrderedDict[tuple, list]' = OrderedDict()
    for r in filas:
        fuente = str(r[0]).strip()
        clave = (fuente, str(r[3]).strip()) if fuente in SHOPIFY else (fuente, str(r[4]).strip())
        grupos.setdefault(clave, []).append(r)

    productos, variantes, vistos = [], [], set()
    for (fuente, _), rs in grupos.items():
        r0 = rs[0]
        marca = marca_de(str(r0[1] or ''))
        nombre = nombre_legible(str(r0[3]), marca)
        cat = categoria(str(r0[2] or ''), nombre)
        sub = subcategoria(cat, str(r0[2] or ''), nombre)
        precios = [precio(r[6], fuente) for r in rs]
        if any(p is None for p in precios):
            continue
        disponibles = [not re.search(r'sin stock|agotado', str(r[10] or ''), re.I) for r in rs]
        base = min(precios)
        sku = f"RD-{str(r0[4]).strip()}"
        if sku in vistos:
            continue
        vistos.add(sku)
        imagen = next((str(r[12]).strip() for r in rs if r[12]), None)
        con_variantes = len(rs) > 1
        productos.append({
            'sku': sku, 'nombre': nombre, 'categoria': cat, 'sub': sub, 'familia': str(r0[2] or '').strip().upper(), 'marca': marca,
            'precio': base, 'imagen': imagen, 'stock': STOCK_DISPONIBLE if any(disponibles) else 0, 'activo': base > 0,
        })
        if con_variantes:
            valores = [str(r[5] or '').strip() for r in rs]
            tipo = 'Color' if all(v.lower() in COLORES for v in valores) else 'Presentación'
            for orden, (r, p, disp, v) in enumerate(zip(rs, precios, disponibles, valores), start=1):
                variantes.append({'sku': sku, 'tipo': tipo, 'valor': v or f'Opción {orden}', 'mod': round(p - base, 2),
                                  'stock': STOCK_DISPONIBLE if disp else 0, 'imagen': str(r[12]).strip() if r[12] else None,
                                  'orden': orden})

    # Gama por tercios de precio dentro de cada familia (para Económico / Normal / Premium de las listas)
    por_familia = defaultdict(list)
    for p in productos:
        por_familia[(p['categoria'], p['familia'])].append(p)
    for lista in por_familia.values():
        lista.sort(key=lambda p: p['precio'])
        n = len(lista)
        for i, p in enumerate(lista):
            p['gama'] = 'medio' if n < 3 else ('economico' if i < n / 3 else 'premium' if i >= 2 * n / 3 else 'medio')

    marcas = sorted({p['marca'] for p in productos if p['marca']})
    cuenta = defaultdict(int)
    for p in productos:
        cuenta[p['categoria']] += 1
    resumen = ', '.join(f'{c}: {n}' for c, n in sorted(cuenta.items()))
    activos = sum(p['activo'] for p in productos)
    con_foto = sum(bool(p['imagen']) for p in productos)
    agotados = sum(p['stock'] == 0 for p in productos)
    print(f'{len(productos)} productos ({resumen}); {len(variantes)} variantes; {activos} activos; {con_foto} con foto; '
          f'{agotados} agotados; {len(marcas)} marcas', file=sys.stderr)

    filas_p = ',\n'.join(
        f"  ({sql(p['sku'])}, {sql(p['nombre'])}, {sql(p['categoria'])}, {sql(p['sub'])}, {sql(p['marca'])}, {p['precio']}, {sql(p['imagen'])}, "
        f"{p['stock']}, {sql(p['gama'])}, {'TRUE' if p['activo'] else 'FALSE'})" for p in productos)
    filas_v = ',\n'.join(
        f"  ({sql(v['sku'])}, {sql(v['tipo'])}, {sql(v['valor'])}, {v['mod']}, {v['stock']}, {sql(v['imagen'])}, {v['orden']})"
        for v in variantes)

    contenido = f"""-- Generado por scripts/generar_seed_productos.py desde {os.path.basename(ruta)}
-- {len(productos)} productos ({resumen}), {len(variantes)} variantes, {len(marcas)} marcas.
-- Lo corre DatabaseInitializer una sola vez (queda en seed_runs). Solo agrega códigos que no existan:
-- no cambia ni borra productos, precios o stock que ya estén en la base.

INSERT INTO categories (name) VALUES {', '.join(f'({sql(c)})' for c in sorted(cuenta))} ON CONFLICT (name) DO NOTHING;
INSERT INTO brands (name) VALUES {', '.join(f'({sql(m)})' for m in marcas)} ON CONFLICT (name) DO NOTHING;

CREATE TEMP TABLE _productos (sku VARCHAR(100), nombre TEXT, categoria VARCHAR(100), sub VARCHAR(100), marca VARCHAR(100), precio DECIMAL(10,2),
                              imagen TEXT, stock INT, gama VARCHAR(20), activo BOOLEAN) ON COMMIT DROP;
INSERT INTO _productos VALUES
{filas_p};

INSERT INTO products (name, category, subcategory, brand, sku, base_price, image_url, stock, tier, is_active)
SELECT nombre, categoria, sub, marca, sku, precio, imagen, stock, gama, activo
FROM _productos c
WHERE NOT EXISTS (SELECT 1 FROM products p WHERE p.sku = c.sku);
"""
    if variantes:
        contenido += f"""
-- Presentaciones y colores del mismo producto: variantes con su precio (sobre el precio base), foto y stock
CREATE TEMP TABLE _variantes (sku VARCHAR(100), tipo VARCHAR(100), valor VARCHAR(150), extra DECIMAL(10,2), stock INT,
                              imagen TEXT, orden INT) ON COMMIT DROP;
INSERT INTO _variantes VALUES
{filas_v};

INSERT INTO product_variant_types (product_id, name, display_order)
SELECT DISTINCT p.id, v.tipo, 0
FROM _variantes v JOIN products p ON p.sku = v.sku
WHERE NOT EXISTS (SELECT 1 FROM product_variant_types t WHERE t.product_id = p.id AND t.name = v.tipo);

INSERT INTO product_variant_values (variant_type_id, value, image_url, price_modifier, stock, display_order)
SELECT t.id, v.valor, v.imagen, v.extra, v.stock, v.orden
FROM _variantes v
JOIN products p ON p.sku = v.sku
JOIN product_variant_types t ON t.product_id = p.id AND t.name = v.tipo
WHERE NOT EXISTS (SELECT 1 FROM product_variant_values x WHERE x.variant_type_id = t.id AND x.value = v.valor);
"""
    destino = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'backend', 'UtilesApi', 'Infrastructure', 'Database',
                           f'seed-productos-{slug(nombre_seed)}.sql')
    with open(destino, 'w', encoding='utf-8') as f:
        f.write(contenido)
    print(f'Escrito {os.path.normpath(destino)}', file=sys.stderr)

    # Subcategorías para una base donde el catálogo ya se cargó (solo completa las vacías)
    subs = defaultdict(int)
    for p in productos:
        subs[(p['categoria'], p['sub'])] += 1
    print(f'{len(subs)} subcategorías: ' + '; '.join(f'{c} › {s} ({n})' for (c, s), n in sorted(subs.items())), file=sys.stderr)
    filas_s = ',\n'.join(f"  ({sql(p['sku'])}, {sql(p['sub'])})" for p in productos)
    destino_s = destino.replace('.sql', '-subcategorias.sql')
    with open(destino_s, 'w', encoding='utf-8') as f:
        f.write(f"""-- Generado por scripts/generar_seed_productos.py: subcategoría de cada producto del catálogo, por su código.
-- Lo corre DatabaseInitializer una sola vez. Solo completa productos sin subcategoría (no pisa lo editado en el panel).

UPDATE products p SET subcategory = v.sub, updated_at = NOW()
FROM (VALUES
{filas_s}
) AS v(sku, sub)
WHERE p.sku = v.sku AND (p.subcategory IS NULL OR p.subcategory = '');
""")
    print(f'Escrito {os.path.normpath(destino_s)}', file=sys.stderr)


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
