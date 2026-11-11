#!/usr/bin/env python3
"""
Genera backend/UtilesApi/Infrastructure/Database/seed-contactos-colegios.sql con los teléfonos y correos publicados
de los colegios (Excel "Colegios_Arequipa_ciudad_2026" con las columnas "Teléfono publicado" y "Correo publicado").

Uso:  python3 scripts/generar_seed_contactos.py ruta/al/archivo.xlsx [--departamento Cusco]

- Sin --departamento escribe seed-contactos-colegios.sql (Arequipa); con él, seed-contactos-<departamento>.sql,
  que DatabaseInitializer corre una sola vez después de los colegios.

- El colegio se reconoce por cualquiera de sus códigos modulares.
- WhatsApp: el primer celular (9 dígitos que empiezan con 9). Teléfono: el primer fijo ("054 232540").
  Los demás números quedan en las notas.
- Solo completa datos vacíos: no cambia lo que ya se editó en el panel.
"""
import os
import re
import sys
import unicodedata

import openpyxl

CORREO = re.compile(r'^[^\s@]+@[^\s@]+\.[^\s@]{2,}$')


def numeros(texto: str) -> list[str]:
    """Separa "054-289541 - 959223679 / 959920469" en números: celulares de 9 dígitos y fijos con código 054."""
    salida = []
    for parte in re.split(r'[/;,]|\s-\s|\s+y\s+', texto):
        d = re.sub(r'\D', '', parte)
        if d.startswith('51') and len(d) == 11:
            d = d[2:]
        # "054-222587-054-222112" llega pegado: se corta en bloques de 9
        while len(d) >= 18:
            salida.append(d[:9])
            d = d[9:]
        if len(d) >= 6:
            salida.append(d)
    vistos = []
    for n in salida:
        if n not in vistos:
            vistos.append(n)
    return vistos


def es_celular(n: str) -> bool:
    return len(n) == 9 and n.startswith('9')


def fijo(n: str) -> str:
    return f'{n[:3]} {n[3:]}' if n.startswith('0') and len(n) >= 9 else n


def sql(v) -> str:
    return 'NULL' if v is None or v == '' else "'" + str(v).replace("'", "''") + "'"


def main(ruta: str, archivo: str = "seed-contactos-colegios.sql"):
    wb = openpyxl.load_workbook(ruta, data_only=True)
    filas, con_whatsapp, con_fijo, con_correo = [], 0, 0, 0
    for r in wb['Colegios EBR'].iter_rows(min_row=6, values_only=True):
        if not r[0]:
            continue
        codigos = [c.strip() for c in str(r[6] or '').split(',') if c.strip()]
        tel = str(r[9] or '').strip()
        correo = str(r[10] or '').strip().lower()
        if not codigos or (not tel and not correo):
            continue
        nums = numeros(tel) if tel else []
        celulares = [n for n in nums if es_celular(n)]
        fijos = [fijo(n) for n in nums if not es_celular(n)]
        whatsapp = celulares[0] if celulares else None
        telefono = fijos[0] if fijos else None
        otros = [n for n in celulares[1:]] + fijos[1:]
        if correo and not CORREO.match(correo):
            correo = ''
        notas = 'Contacto público (padrón y fichas web, 10/11/2026); confirmar que sea el canal vigente.'
        if otros:
            notas += ' Otros teléfonos: ' + ', '.join(otros) + '.'
        con_whatsapp += bool(whatsapp)
        con_fijo += bool(telefono)
        con_correo += bool(correo)
        arr = 'ARRAY[' + ', '.join(sql(c) for c in codigos) + ']::text[]'
        filas.append(f"  ({arr}, {sql(whatsapp)}, {sql(telefono)}, {sql(correo or None)}, {sql(notas)})")

    print(f'{len(filas)} sedes con contacto: {con_whatsapp} WhatsApp, {con_fijo} fijos, {con_correo} correos', file=sys.stderr)
    contenido = f"""-- Generado por scripts/generar_seed_contactos.py desde {os.path.basename(ruta)}
-- {len(filas)} sedes con contacto público: {con_whatsapp} WhatsApp, {con_fijo} teléfonos fijos y {con_correo} correos.
-- Lo corre DatabaseInitializer una sola vez (queda anotado en seed_runs).
-- Solo completa campos vacíos: no cambia lo que ya se editó en Admin > Pedir listas.

CREATE TEMP TABLE _contactos (codigos TEXT[], whatsapp VARCHAR(20), telefono VARCHAR(30), correo VARCHAR(150), notas TEXT) ON COMMIT DROP;

INSERT INTO _contactos VALUES
""" + ',\n'.join(filas) + """;

-- Un colegio puede tener varias sedes en el Excel (primaria y secundaria): se toma el primer dato de cada tipo
UPDATE schools s SET
    whatsapp = COALESCE(s.whatsapp, c.whatsapp),
    telefono = COALESCE(s.telefono, c.telefono),
    correo = COALESCE(s.correo, c.correo),
    contacto_notas = COALESCE(s.contacto_notas, c.notas)
FROM (
    SELECT DISTINCT ON (s2.id) s2.id,
           (SELECT x.whatsapp FROM _contactos x WHERE x.codigos && string_to_array(replace(s2.codigos_modulares, ' ', ''), ',') AND x.whatsapp IS NOT NULL LIMIT 1) AS whatsapp,
           (SELECT x.telefono FROM _contactos x WHERE x.codigos && string_to_array(replace(s2.codigos_modulares, ' ', ''), ',') AND x.telefono IS NOT NULL LIMIT 1) AS telefono,
           (SELECT x.correo FROM _contactos x WHERE x.codigos && string_to_array(replace(s2.codigos_modulares, ' ', ''), ',') AND x.correo IS NOT NULL LIMIT 1) AS correo,
           (SELECT x.notas FROM _contactos x WHERE x.codigos && string_to_array(replace(s2.codigos_modulares, ' ', ''), ',') LIMIT 1) AS notas
    FROM schools s2
    WHERE s2.codigos_modulares IS NOT NULL
) c
WHERE s.id = c.id AND (c.whatsapp IS NOT NULL OR c.telefono IS NOT NULL OR c.correo IS NOT NULL);
"""
    destino = f'backend/UtilesApi/Infrastructure/Database/{archivo}'
    with open(destino, 'w', encoding='utf-8') as f:
        f.write(contenido)
    print(f'Escrito {destino}', file=sys.stderr)


def slug(texto: str) -> str:
    t = unicodedata.normalize('NFD', texto.lower())
    return re.sub(r'[^a-z0-9]+', '-', ''.join(c for c in t if unicodedata.category(c) != 'Mn')).strip('-')


if __name__ == '__main__':
    args = sys.argv[1:]
    archivo = 'seed-contactos-colegios.sql'
    if '--departamento' in args:
        i = args.index('--departamento')
        if i + 1 >= len(args):
            sys.exit(__doc__)
        if args[i + 1].strip().lower() != 'arequipa':
            archivo = f'seed-contactos-{slug(args[i + 1])}.sql'
        del args[i:i + 2]
    if len(args) != 1:
        sys.exit(__doc__)
    main(args[0], archivo)
