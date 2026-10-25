"""The data-format ids: contracts/schema_ids.py, the repository's one Python table, loaded by its path (contracts/README.md,
"Schema ids"). The exporter writes the new ids."""
import importlib.util
from pathlib import Path

# pipeline/landscape/export/ids.py -> the repository root is three levels above this folder.
_TABLE = Path(__file__).resolve().parents[3] / 'contracts' / 'schema_ids.py'
_spec = importlib.util.spec_from_file_location('garage_game_schema_ids', _TABLE)
TABLE = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(TABLE)
id_of = TABLE.id_of
# Every GLB the exporter writes (glTF asset.generator, free text). It was "EnFractal landscape room exporter v1"; it names no
# game now, because no output byte may spell a source label (test_export's privacy check) and "Garage" labels every garage.
GLB_GENERATOR = 'landscape room exporter v1'
