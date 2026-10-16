"""Build local display data from current config JSON and an I2 localization blob.

The generated catalog is deployment data, never source-controlled.
"""
import argparse
import ast
import hashlib
import json
import pathlib
import re
import struct


def localization(raw):
    terms = {}
    pattern = re.compile(rb'(?:Talent(?:Desc)?|CardName|CharacterName|FateStrategy(?:Name|Desc)|XianMoStrategy(?:Name|Desc)|ResonanceTalent(?:Desc)?|Buff(?:Desc)?|KeYinCard(?:Name|Desc)?)_\d+')
    for offset in range(0, len(raw) - 16, 4):
        size = struct.unpack_from('<i', raw, offset)[0]
        if not 3 <= size <= 100 or offset + size + 12 > len(raw):
            continue
        key = raw[offset + 4:offset + 4 + size]
        if not pattern.fullmatch(key):
            continue
        pos = offset + 4 + size + (-size % 4)
        _, count = struct.unpack_from('<ii', raw, pos)
        pos += 8
        if not 1 <= count <= 20:
            continue
        try:
            values = []
            for _ in range(count):
                length = struct.unpack_from('<i', raw, pos)[0]
                if not 0 <= length <= 20000 or pos + length + 4 > len(raw):
                    raise ValueError('Invalid language record')
                values.append(raw[pos + 4:pos + 4 + length].decode('utf-8'))
                pos += 4 + length + (-length % 4)
            terms.setdefault(key.decode('ascii'), values[0])
        except (UnicodeDecodeError, struct.error, ValueError):
            continue
    if len(terms) < 100:
        raise ValueError('Localization terms missing')
    return terms


def expression(text, row):
    """Only numeric configuration expressions; no calls, attributes or eval."""
    if text.strip() in row and type(row[text.strip()]) in (int, float):
        return str(row[text.strip()])
    def visit(node):
        if isinstance(node, ast.Constant) and type(node.value) in (int, float):
            return node.value
        if isinstance(node, ast.Name) and node.id in row:
            return row[node.id]
        if isinstance(node, ast.Subscript):
            return visit(node.value)[visit(node.slice)]
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub):
            return -visit(node.operand)
        if isinstance(node, ast.BinOp):
            a, b = visit(node.left), visit(node.right)
            if isinstance(node.op, ast.Add): return a + b
            if isinstance(node.op, ast.Sub): return a - b
            if isinstance(node.op, ast.Mult): return a * b
            if isinstance(node.op, ast.Div): return int(a / b)
        raise ValueError('Contextual effect parameter')
    value = visit(ast.parse(text, mode='eval').body)
    if type(value) not in (int, float):
        raise ValueError('Not a scalar')
    return str(value)


def describe(template, row, card_names, terms=None):
    unresolved = []
    def replace(match):
        try:
            return expression(match[1], row)
        except (ValueError, TypeError, KeyError, IndexError, SyntaxError, ZeroDivisionError):
            unresolved.append(match[1])
            return match[0]
    text = re.sub(r'\{([^{}]+)\}', replace, template)
    text = text.replace(chr(92) + 'n', chr(10))
    text = re.sub(r'<[^>]*>', '', text)
    text = re.sub(r'【KEYWORD_([^】]+)】', lambda m: '【' + m[1] + '】', text)
    text = re.sub(r'【TALENT_(\d+)】', lambda m: '【' + (terms or {}).get('Talent_' + m[1], '仙命 ' + m[1]) + '】', text)
    text = re.sub(r'【(-?\d+)】', lambda m: '【' + card_names.get(int(m[1]), m[1]) + '】', text)
    return text, sorted(set(unresolved))


def build(config_dir, raw, config_version):
    terms = localization(raw)
    def load(name):
        return json.loads((config_dir / (name + '.json')).read_text(encoding='utf-8-sig'))
    cards = load('CardConfig')
    card_names = {row.get('id', 0): row.get('name', str(row.get('id', 0))) for row in cards}
    kinds = {}
    sources = [('talent', 'TalentConfig', 'Talent_', 'TalentDesc_'),
               ('fate', 'FateStrategyConfig', 'FateStrategyName_', 'FateStrategyDesc_'),
               ('xianmo', 'XianMoStrategyConfig', 'XianMoStrategyName_', 'XianMoStrategyDesc_'),
               ('resonance', 'TalentResonanceConfig', 'ResonanceTalent_', 'ResonanceTalentDesc_'),
               ('keyin', 'KeYinCardConfig', 'KeYinCardName_', 'KeYinCardDesc_'),
               ('buff', 'BuffConfig', 'Buff_', 'BuffDesc_'),
               ('character', 'CharacterConfig', 'CharacterName_', ''),
               ('card', 'CardConfig', 'CardName_', '')]
    for kind, source, name_prefix, desc_prefix in sources:
        rows = []
        for config in load(source):
            identifier = config.get('id', config.get('type', 0))
            name_id = config.get('talentId', identifier) if kind == 'talent' else identifier
            name = terms.get(name_prefix + str(name_id)) or config.get('name') or ''
            template = terms.get(desc_prefix + str(identifier), '') if desc_prefix else ''
            template = template or config.get('desc', '')
            row = dict(config)
            row.setdefault('otherParams', [])
            for key in ('countParam', 'effectRound', 'relateTalentId', 'maxHp', 'attack', 'def', 'attackCount', 'anima', 'physique'):
                row.setdefault(key, 0)
            description, unresolved = describe(template, row, card_names, terms)
            entry = {'id': identifier, 'kind': kind, 'name': name,
                     'description': description, 'descriptionTemplate': template,
                     'known': True, 'nameAvailable': bool(name),
                     'descriptionAvailable': bool(template), 'unresolvedFields': unresolved}
            for key in ('talentId', 'level', 'sect', 'charId', 'otherParams', 'effectRound', 'countParam', 'category', 'npc', 'isAvailable', 'isBattleEffect'):
                if key in config: entry[key] = config[key]
            rows.append(entry)
        kinds[kind] = rows
    result = {'gameRecordVersion': '001.0008.0002', 'configVersion': config_version,
              'localizationSha256': hashlib.sha256(raw).hexdigest(), 'kinds': kinds}
    result['version'] = hashlib.sha256(json.dumps(result, ensure_ascii=False, sort_keys=True).encode()).hexdigest()[:20]
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', required=True, type=pathlib.Path)
    parser.add_argument('--localization', required=True, type=pathlib.Path)
    parser.add_argument('--battle-config', required=True, type=pathlib.Path)
    parser.add_argument('--out', required=True, type=pathlib.Path)
    args = parser.parse_args()
    config_version = json.loads(args.battle_config.read_text(encoding='utf-8-sig'))['version']
    result = build(args.config, args.localization.read_bytes(), config_version)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    print('Catalog', result['version'], {kind: len(rows) for kind, rows in result['kinds'].items()})
