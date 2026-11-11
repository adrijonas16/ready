#!/usr/bin/env python3
"""
Genera un seed de colegios (con sus grados y contactos publicados) a partir de un Excel con el formato de
"Colegios_Lima_Metropolitana_y_Callao_2026.xlsx": hojas "Colegios EBR" (una fila por institución y distrito)
y "Todos los servicios" (una fila por código modular, con el departamento).

Uso:  python3 scripts/generar_seed_padron_distritos.py ruta/al/archivo.xlsx nombre-del-seed
      → backend/UtilesApi/Infrastructure/Database/seed-colegios-<nombre-del-seed>.sql

- El departamento sale de la hoja "Todos los servicios" (la provincia Callao es el departamento Callao).
- Un colegio por institución y distrito: "0001 María Auxiliadora" de Lima y de Los Olivos son colegios distintos.
- Sin dirección en el archivo: se guarda el distrito y la provincia como dirección de referencia.
- Grados: inicial 3, 4 y 5 años (jardín, cuna-jardín y programas no escolarizados), 1.º a 6.º de primaria y
  1.º a 5.º de secundaria. Las cunas solo de 0 a 2 años quedan sin grados.
- Contactos: el primer celular va a WhatsApp, el primer fijo a teléfono (con el código de la provincia); los demás
  números y la ficha web quedan en las notas. Solo completa campos vacíos.
- DatabaseInitializer lo corre una sola vez al arrancar la API. La tienda solo muestra estos colegios cuando su
  departamento está atendido (Arequipa siempre; los demás con "Envíos a todo el Perú" activado).
"""
import os
import re
import sys
from collections import OrderedDict

import openpyxl

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from generar_seed_colegios import DEPARTAMENTOS, INICIAL, ORDEN, PRIMARIA, SECUNDARIA, nombre_colegio, slug, sql, titulo  # noqa: E402

CORREO = re.compile(r'^[^\s@]+@[^\s@]+\.[^\s@]{2,}$')
# Código de larga distancia de las provincias con más colegios (los fijos de 7 dígitos son de Lima y Callao)
CODIGO_FIJO = {'Lima': '01', 'Callao': '01'}


def departamento_oficial(texto: str) -> str:
    d = next((x for x in DEPARTAMENTOS if slug(x) == slug(texto or '')), None)
    if not d:
        sys.exit(f'Departamento desconocido en el Excel: {texto!r}')
    return d


def grados(texto: str) -> list[str]:
    t = (texto or '').lower()
    g = []
    if '3–5 años' in t or '3-5 años' in t or '0–5 años' in t or '0-5 años' in t or 'no escolarizado' in t:
        g += INICIAL
    if 'primaria' in t:
        g += PRIMARIA
    if 'secundaria' in t:
        g += SECUNDARIA
    return g


def numeros(texto) -> list[str]:
    """"5944010 - 5786561 / 987654321" → ['5944010', '5786561', '987654321']"""
    salida = []
    for parte in re.split(r'[/;,]|\s-\s|\s+y\s+', str(texto or '')):
        d = re.sub(r'\D', '', parte)
        if d.startswith('51') and len(d) == 11:
            d = d[2:]
        if 6 <= len(d) <= 9 and d not in salida:
            salida.append(d)
    return salida


def main(ruta: str, nombre: str):
    wb = openpyxl.load_workbook(ruta, read_only=True, data_only=True)
    dep_por_codigo = {}
    for r in wb['Todos los servicios'].iter_rows(min_row=6, values_only=True):
        if r[0]:
            dep_por_codigo[str(r[0]).strip()] = departamento_oficial(str(r[7]))

    colegios: 'OrderedDict[tuple, dict]' = OrderedDict()
    for r in wb['Colegios EBR'].iter_rows(min_row=6, values_only=True):
        if not r[0]:
            continue
        nombre_col = nombre_colegio(str(r[0]))
        distrito = titulo(str(r[4] or '').strip()) or None
        provincia = titulo(str(r[5] or '').strip()) or None
        codigos = [c.strip() for c in str(r[6] or '').split(',') if c.strip()]
        if not codigos:
            continue
        departamento = dep_por_codigo.get(codigos[0]) or departamento_oficial(provincia or '')
        clave = (nombre_col.lower(), departamento, (distrito or '').lower())
        c = colegios.setdefault(clave, {'nombre': nombre_col, 'departamento': departamento, 'provincia': provincia, 'distrito': distrito,
                                        'codigos': [], 'gestion': set(), 'grados': set(), 'numeros': [], 'correo': None, 'ficha': None})
        c['codigos'] += [x for x in codigos if x not in c['codigos']]
        c['gestion'].update(x.strip() for x in str(r[3] or '').split('+') if x.strip())
        c['grados'].update(grados(str(r[2] or '')))
        c['numeros'] += [n for n in numeros(r[8]) if n not in c['numeros']]
        correo = str(r[9] or '').strip().lower()
        if not c['correo'] and CORREO.match(correo):
            c['correo'] = correo
        if not c['ficha'] and r[11]:
            c['ficha'] = str(r[11]).strip()

    filas, con_wsp, con_fijo, con_correo, total_grados = [], 0, 0, 0, 0
    por_dep: dict[str, int] = {}
    for c in colegios.values():
        celulares = [n for n in c['numeros'] if len(n) == 9 and n.startswith('9')]
        fijos = [n for n in c['numeros'] if n not in celulares]
        whatsapp = celulares[0] if celulares else None
        codigo = CODIGO_FIJO.get(c['provincia'] or '', '')
        telefono = (f'{codigo} {fijos[0]}'.strip() if len(fijos[0]) <= 7 else fijos[0]) if fijos else None
        otros = [n for n in c['numeros'] if n not in (whatsapp, fijos[0] if fijos else None)]
        notas = '\n'.join(x for x in [f"Otros números: {', '.join(otros)}" if otros else '', f"Ficha: {c['ficha']}" if c['ficha'] else ''] if x) or None
        con_wsp += bool(whatsapp)
        con_fijo += bool(telefono)
        con_correo += bool(c['correo'])
        lista = [g for g in ORDEN if g in c['grados']]
        total_grados += len(lista)
        por_dep[c['departamento']] = por_dep.get(c['departamento'], 0) + 1
        direccion = ', '.join(x for x in [c['distrito'], c['provincia'] if c['provincia'] != c['distrito'] else None] if x)
        arr = 'ARRAY[' + ', '.join(sql(g) for g in lista) + ']::text[]'
        filas.append(f"  ({sql(c['codigos'][0])}, {sql(', '.join(c['codigos']))}, {sql(c['nombre'])}, {sql(direccion or None)}, "
                     f"{sql(' + '.join(sorted(c['gestion'])))}, {arr}, {sql(c['departamento'])}, {sql(c['provincia'])}, {sql(c['distrito'])}, "
                     f"{sql(whatsapp)}, {sql(telefono)}, {sql(c['correo'])}, {sql(notas)})")

    resumen = ', '.join(f'{d}: {n}' for d, n in sorted(por_dep.items()))
    print(f'{len(filas)} colegios ({resumen}), {total_grados} grados; {con_wsp} WhatsApp, {con_fijo} fijos, {con_correo} correos', file=sys.stderr)

    contenido = f"""-- Generado por scripts/generar_seed_padron_distritos.py desde {os.path.basename(ruta)}
-- {len(filas)} colegios ({resumen}) y {total_grados} grados; contactos publicados: {con_wsp} WhatsApp, {con_fijo} fijos, {con_correo} correos.
-- Lo corre DatabaseInitializer una sola vez (queda anotado en seed_runs).
-- Solo agrega lo que falta: no cambia ni borra colegios, grados o contactos que ya estén en la base.

CREATE TEMP TABLE _colegios (codigo VARCHAR(10), codigos TEXT, nombre TEXT, direccion TEXT, gestion TEXT, grados TEXT[],
                             departamento VARCHAR(40), provincia VARCHAR(80), distrito VARCHAR(80),
                             whatsapp VARCHAR(20), telefono VARCHAR(30), correo VARCHAR(150), notas TEXT) ON COMMIT DROP;

INSERT INTO _colegios VALUES
""" + ',\n'.join(filas) + """;

-- Colegios (se reconocen por su código modular o por nombre + departamento + distrito)
INSERT INTO schools (name, address, codigo_modular, codigos_modulares, gestion, departamento, provincia, distrito)
SELECT c.nombre, c.direccion, c.codigo, c.codigos, c.gestion, c.departamento, c.provincia, c.distrito
FROM _colegios c
WHERE NOT EXISTS (SELECT 1 FROM schools s WHERE s.codigo_modular = c.codigo
                  OR (lower(s.name) = lower(c.nombre) AND s.departamento = c.departamento
                      AND lower(COALESCE(s.distrito, '')) = lower(COALESCE(c.distrito, ''))));

-- Contactos publicados (solo completa campos vacíos)
UPDATE schools s SET
    whatsapp = COALESCE(s.whatsapp, c.whatsapp),
    telefono = COALESCE(s.telefono, c.telefono),
    correo = COALESCE(s.correo, c.correo),
    contacto_notas = COALESCE(s.contacto_notas, c.notas)
FROM _colegios c
WHERE s.codigo_modular = c.codigo;

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
    destino = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'backend', 'UtilesApi', 'Infrastructure', 'Database',
                           f'seed-colegios-{slug(nombre)}.sql')
    with open(destino, 'w', encoding='utf-8') as f:
        f.write(contenido)
    print(f'Escrito {os.path.normpath(destino)}', file=sys.stderr)


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
