#!/usr/bin/env python3
"""
Genera backend/UtilesApi/Infrastructure/Database/seed-colegios-arequipa.sql a partir del Excel
"Colegios_Arequipa_ciudad_2026.xlsx" (padrón MINEDU, distrito de Arequipa).

Uso:  python3 scripts/generar_seed_colegios.py ruta/al/Colegios_Arequipa_ciudad_2026.xlsx

- Una fila por colegio (las sedes con el mismo nombre se juntan: p. ej. De La Salle primaria + secundaria).
- La dirección y la ubicación salen de la hoja "Todos los servicios" (por código modular).
- Grados: inicial 3, 4 y 5 años (también para cuna-jardín y programas no escolarizados),
  1.º a 6.º de primaria y 1.º a 5.º de secundaria, con los mismos nombres de la tabla sections.
"""
import re
import sys
from collections import OrderedDict

import openpyxl

MINUSCULAS = {'de', 'del', 'la', 'las', 'los', 'el', 'y', 'e', 'en', 'a', 'al', 'para', 'con', 'o'}
SIGLAS = {'IE', 'I.E.', 'IEP', 'I.E.P.', 'CEP', 'C.E.P.', 'PNP', 'FAP', 'UNSA', 'UCSM', 'II', 'III', 'IV', 'VI', 'VII', 'VIII', 'IX', 'XI', 'XII', 'XXI', 'XXIII', 'SAC', 'S.A.C.', 'EIRL', 'N°'}

INICIAL = ['Inicial - 3 años', 'Inicial - 4 años', 'Inicial - 5 años']
PRIMARIA = ['1ro Primaria', '2do Primaria', '3ro Primaria', '4to Primaria', '5to Primaria', '6to Primaria']
SECUNDARIA = ['1ro Secundaria', '2do Secundaria', '3ro Secundaria', '4to Secundaria', '5to Secundaria']
ORDEN = INICIAL + PRIMARIA + SECUNDARIA


def titulo(texto: str) -> str:
    palabras = re.split(r'(\s+)', texto.strip())
    salida, primera = [], True
    for p in palabras:
        if not p or p.isspace():
            salida.append(p)
            continue
        if p.upper() in SIGLAS:
            salida.append(p.upper())
        elif not primera and p.lower() in MINUSCULAS:
            salida.append(p.lower())
        else:
            # Respeta guiones y apóstrofos: "SANTA ROSA-LIMA" → "Santa Rosa-Lima"
            salida.append('-'.join(s[:1].upper() + s[1:].lower() for s in p.split('-')))
        # Después de un número o una sigla ("40002 AL AIRE LIBRE") la palabra sigue contando como la primera
        primera = primera and (p.isdigit() or p.upper() in SIGLAS)
    return ''.join(salida)


def nombre_colegio(crudo: str) -> str:
    n = titulo(crudo)
    # Los públicos se registran con su número: "40001 Luis H. Bouroncle" → "I.E. 40001 Luis H. Bouroncle"
    return f'I.E. {n}' if re.match(r'^\d', n) else n


def grados(cobertura: str) -> list[str]:
    c = cobertura.lower()
    g = []
    if 'años' in c or 'inicial' in c:
        g += INICIAL
    if 'primaria' in c:
        g += PRIMARIA
    if 'secundaria' in c:
        g += SECUNDARIA
    return g


def sql(v) -> str:
    if v is None or v == '':
        return 'NULL'
    if isinstance(v, (int, float)):
        return repr(v)
    return "'" + str(v).replace("'", "''") + "'"


def main(ruta: str):
    wb = openpyxl.load_workbook(ruta, data_only=True)
    servicios = {}
    for r in wb['Todos los servicios'].iter_rows(min_row=6, values_only=True):
        if r[0]:
            servicios[str(r[0]).strip()] = {'direccion': r[6], 'lat': r[7], 'lng': r[8]}

    colegios: 'OrderedDict[str, dict]' = OrderedDict()
    for r in wb['Colegios EBR'].iter_rows(min_row=6, values_only=True):
        if not r[0]:
            continue
        nombre = nombre_colegio(str(r[0]))
        codigos = [c.strip() for c in str(r[6] or '').split(',') if c.strip()]
        c = colegios.setdefault(nombre, {'codigos': [], 'gestion': set(), 'grados': set(), 'direccion': None, 'lat': None, 'lng': None})
        c['codigos'] += [x for x in codigos if x not in c['codigos']]
        c['gestion'].update(x.strip() for x in str(r[3] or '').split('+') if x.strip())
        c['grados'].update(grados(str(r[2] or '')))
        for cod in codigos:
            s = servicios.get(cod)
            if s and not c['direccion'] and s['direccion']:
                c['direccion'] = s['direccion']
            if s and c['lat'] is None and s['lat'] is not None:
                c['lat'], c['lng'] = s['lat'], s['lng']
        if not c['direccion'] and r[5]:
            c['direccion'] = r[5]

    filas = []
    for nombre, c in colegios.items():
        direccion = f"{titulo(str(c['direccion']))}, Arequipa" if c['direccion'] else 'Arequipa'
        lista = [g for g in ORDEN if g in c['grados']]
        arr = 'ARRAY[' + ', '.join(sql(g) for g in lista) + ']::text[]'
        filas.append(f"  ({sql(c['codigos'][0])}, {sql(', '.join(c['codigos']))}, {sql(nombre)}, {sql(direccion)}, "
                     f"{sql(' + '.join(sorted(c['gestion'])))}, {sql(c['lat'])}, {sql(c['lng'])}, {arr})")

    total_grados = sum(len(c['grados']) for c in colegios.values())
    print(f'{len(colegios)} colegios, {total_grados} grados', file=sys.stderr)
    cabecera = f"""-- Generado por scripts/generar_seed_colegios.py desde Colegios_Arequipa_ciudad_2026.xlsx
-- (padrón MINEDU, distrito de Arequipa). {len(colegios)} colegios y {total_grados} grados.
-- Lo corre DatabaseInitializer una sola vez (marca 'colegios-arequipa-2026' en seed_runs).
-- Solo agrega lo que falta: no cambia ni borra colegios o grados que ya estén en la base.
"""
    cuerpo = """
CREATE TEMP TABLE _colegios (codigo VARCHAR(10), codigos TEXT, nombre TEXT, direccion TEXT, gestion TEXT,
                             latitud DOUBLE PRECISION, longitud DOUBLE PRECISION, grados TEXT[]) ON COMMIT DROP;

INSERT INTO _colegios VALUES
""" + ',\n'.join(filas) + """;

-- Colegios (se reconocen por su código modular principal o, si se crearon a mano, por el nombre)
INSERT INTO schools (name, address, codigo_modular, codigos_modulares, gestion, latitud, longitud)
SELECT c.nombre, c.direccion, c.codigo, c.codigos, c.gestion, c.latitud, c.longitud
FROM _colegios c
WHERE NOT EXISTS (SELECT 1 FROM schools s WHERE s.codigo_modular = c.codigo OR lower(s.name) = lower(c.nombre));

-- A los que ya existían por nombre se les completan los datos del padrón (sin tocar nombre ni dirección)
UPDATE schools s SET codigo_modular = c.codigo, codigos_modulares = c.codigos, gestion = c.gestion,
                     latitud = COALESCE(s.latitud, c.latitud), longitud = COALESCE(s.longitud, c.longitud)
FROM _colegios c
WHERE s.codigo_modular IS NULL AND lower(s.name) = lower(c.nombre)
  AND NOT EXISTS (SELECT 1 FROM schools o WHERE o.codigo_modular = c.codigo);

-- Niveles que ofrece cada colegio (filtro de la página de listas)
INSERT INTO school_sections (school_id, section_id)
SELECT s.id, x.id
FROM _colegios c
JOIN schools s ON s.codigo_modular = c.codigo
CROSS JOIN LATERAL unnest(c.grados) AS g(nombre)
JOIN sections x ON x.name = g.nombre
ON CONFLICT DO NOTHING;

-- Un grado por cada nivel del colegio, para el año escolar en curso (desde octubre se prepara el siguiente)
INSERT INTO grades (school_id, name, year)
SELECT s.id, g.nombre, a.anio
FROM _colegios c
JOIN schools s ON s.codigo_modular = c.codigo
CROSS JOIN LATERAL unnest(c.grados) AS g(nombre)
CROSS JOIN (SELECT (EXTRACT(YEAR FROM NOW()) + CASE WHEN EXTRACT(MONTH FROM NOW()) >= 10 THEN 1 ELSE 0 END)::int AS anio) a
WHERE NOT EXISTS (SELECT 1 FROM grades x WHERE x.school_id = s.id AND lower(x.name) = lower(g.nombre) AND x.year = a.anio);
"""
    destino = 'backend/UtilesApi/Infrastructure/Database/seed-colegios-arequipa.sql'
    with open(destino, 'w', encoding='utf-8') as f:
        f.write(cabecera + cuerpo)
    print(f'Escrito {destino}', file=sys.stderr)


if __name__ == '__main__':
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
