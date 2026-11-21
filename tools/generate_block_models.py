"""Build the original block models as single-primitive GLBs with a shared color palette."""

import json
import struct
import zlib
from pathlib import Path

MODELS = Path(__file__).resolve().parents[1] / "src/Models"
COLORS = {
    "ink": "232938", "navy": "344457", "blue": "496078",
    "teal": "498984", "mint": "9BD4AF", "sand": "D3AA76",
    "skin": "B67C59", "light": "D6946B", "hair": "48312A",
    "sole": "333038", "wood": "815136", "trim": "B27843",
    "wrap": "CAA26B", "slate": "637886", "stone": "839BA2",
    "iron": "A8BABF", "edge": "D3E3DF", "brass": "D2AA56",
    "orange": "F69447", "purple": "7E679A", "glow": "BBF6E2",
}
WEAPON_COLORS = {
    **COLORS,
    "copper": "AA542F", "amber": "FFD083", "steel": "748798",
    "jade": "27625E", "ceramic": "BBC5C3", "charcoal": "191E27",
}
FACES = (
    ((-1, 0, 0), (0, 4, 6, 2)), ((1, 0, 0), (1, 3, 7, 5)),
    ((0, -1, 0), (0, 1, 5, 4)), ((0, 1, 0), (2, 6, 7, 3)),
    ((0, 0, -1), (0, 2, 3, 1)), ((0, 0, 1), (4, 5, 7, 6)),
)


def palette_png(colors):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    pixels = b"".join(bytes.fromhex(color) * 4 for color in colors.values())
    return (b"\x89PNG\r\n\x1a\n" +
            chunk(b"IHDR", struct.pack(">IIBBBBB", len(colors) * 4, 4, 8, 2, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress((b"\0" + pixels) * 4)) +
            chunk(b"IEND", b""))


def save(path, boxes, colors=COLORS):
    positions, normals, uvs, indices = [], [], [], []
    palette = list(colors)
    for x, y, z, width, height, depth, color in boxes:
        corners = [(x + sx * width / 2, y + sy * height / 2, z + sz * depth / 2)
                   for sz in (-1, 1) for sy in (-1, 1) for sx in (-1, 1)]
        uv = ((palette.index(color) + .5) / len(palette), .5)
        # The GLB importer reflects Z. Write right-handed asset coordinates
        # so the preview, articulated joints and pulse muzzle all use +Z in game.
        for (nx, ny, nz), corners_on_face in FACES:
            start = len(positions)
            positions.extend((corners[i][0], corners[i][1], -corners[i][2])
                             for i in corners_on_face)
            normals.extend([(nx, ny, -nz)] * 4)
            uvs.extend([uv] * 4)
            indices.extend(start + i for i in (0, 2, 1, 0, 3, 2))

    data = bytearray()
    views, accessors = [], []

    def view(raw):
        data.extend(b"\0" * (-len(data) % 4))
        views.append(dict(buffer=0, byteOffset=len(data), byteLength=len(raw)))
        data.extend(raw)
        return len(views) - 1

    for values, dims in ((positions, 3), (normals, 3), (uvs, 2)):
        raw = b"".join(struct.pack("<" + "f" * dims, *v) for v in values)
        accessors.append(dict(bufferView=view(raw), componentType=5126,
                              count=len(values), type=f"VEC{dims}"))
    accessors[0]["min"] = list(map(min, zip(*positions)))
    accessors[0]["max"] = list(map(max, zip(*positions)))
    accessors.append(dict(bufferView=view(struct.pack(f"<{len(indices)}H", *indices)),
                          componentType=5123, count=len(indices), type="SCALAR"))
    atlas = view(palette_png(colors))
    data.extend(b"\0" * (-len(data) % 4))
    doc = dict(asset=dict(version="2.0"), scene=0, scenes=[dict(nodes=[0])],
               nodes=[dict(mesh=0)], meshes=[dict(primitives=[dict(
                   attributes={"POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2},
                   indices=3, material=0)])], materials=[dict(pbrMetallicRoughness=dict(
                       baseColorTexture=dict(index=0), metallicFactor=0, roughnessFactor=.82))],
               images=[dict(bufferView=atlas, mimeType="image/png")],
               textures=[dict(source=0, sampler=0)], samplers=[dict(magFilter=9728, minFilter=9728)],
               bufferViews=views, accessors=accessors, buffers=[dict(byteLength=len(data))])
    encoded = json.dumps(doc, separators=(",", ":")).encode()
    encoded += b" " * (-len(encoded) % 4)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(struct.pack("<5I", 0x46546C67, 2, 28 + len(encoded) + len(data),
                                 len(encoded), 0x4E4F534A) + encoded +
                     struct.pack("<2I", len(data), 0x004E4942) + data)


body = {
    "torso": [(0,.245,0,.46,.48,.28,"teal"), (0,.07,0,.48,.12,.3,"navy"),
              (0,.09,.155,.49,.066,.026,"wood"), (0,.09,.177,.105,.08,.024,"brass"),
              (0,.33,-.175,.33,.29,.14,"ink"), (0,.38,-.248,.3,.085,.038,"trim"),
              (-.155,.27,.153,.074,.4,.025,"sand"), (.155,.27,.153,.074,.4,.025,"sand"),
              (.068,.35,.153,.083,.061,.032,"brass"), (0,.469,.012,.21,.065,.285,"sand")],
    "head": [(0,.205,0,.34,.33,.32,"skin"), (0,.099,.158,.22,.088,.027,"light"),
             (0,.39,0,.43,.145,.4,"blue"), (0,.311,.145,.485,.05,.28,"navy"),
             (0,.466,-.022,.31,.043,.28,"teal"), (0,.353,.207,.4,.054,.029,"sand"),
             (-.17,.207,-.034,.045,.16,.19,"hair"), (.17,.207,-.034,.045,.16,.19,"hair"),
             (-.084,.233,.166,.056,.048,.025,"ink"), (.084,.233,.166,.056,.048,.025,"ink"),
             (0,.181,.176,.064,.057,.048,"light")],
    "upper_arm": [(0,-.11,0,.167,.28,.192,"teal"), (0,-.012,0,.192,.104,.216,"blue"),
                  (0,-.222,0,.175,.059,.201,"sand")],
    "forearm": [(0,-.084,0,.138,.175,.16,"skin"), (0,-.165,0,.156,.064,.17,"wood"),
                (0,-.219,.022,.164,.092,.17,"sand"), (0,-.233,.092,.144,.065,.035,"trim")],
    "thigh": [(0,-.153,0,.212,.339,.218,"blue"), (0,-.18,.11,.159,.141,.026,"navy"),
              (0,-.111,.127,.142,.042,.024,"trim")],
    "shin": [(0,-.126,0,.182,.267,.185,"navy"), (0,-.047,.096,.167,.113,.036,"blue"),
             (0,-.265,.055,.221,.117,.308,"wood"), (0,-.329,.057,.231,.055,.32,"sole"),
             (0,-.265,.195,.202,.09,.057,"trim")],
}
for name, boxes in body.items():
    save(MODELS / "BlockCharacter" / f"{name}.glb", boxes)

for tier, face, edge in (("stone", "slate", "stone"), ("iron", "iron", "edge")):
    for tool in ("pick", "axe", "spade"):
        boxes = [(0,-.035,0,.087,.68,.082,"wood"), (0,-.1,.041,.052,.4,.018,"trim"),
                 (0,-.36,0,.109,.06,.1,"sand"), (0,-.16,0,.101,.225,.099,"ink"),
                 (0,.284,0,.127,.141,.115,"wrap")]
        for y in (-.25,-.19,-.13,-.07,.244,.29,.332):
            boxes.append((0,y,0,.112,.025,.119,"trim"))
        if tool == "pick":
            boxes += [(-.087,.40,0,.325,.119,.137,face), (.119,.40,0,.264,.109,.143,face),
                      (.25,.355,0,.135,.126,.116,face), (.32,.309,0,.103,.119,.095,edge),
                      (.375,.261,0,.077,.102,.07,edge), (-.245,.36,0,.094,.102,.103,edge),
                      (.035,.395,.077,.102,.087,.022,"brass")]
        elif tool == "axe":
            boxes += [(.083,.385,0,.285,.138,.151,face), (.209,.365,0,.153,.246,.126,face),
                      (.29,.36,0,.064,.231,.112,edge), (.327,.36,0,.04,.158,.092,edge),
                      (.211,.508,0,.146,.063,.103,edge), (.208,.226,0,.128,.064,.103,face),
                      (.035,.39,.083,.102,.083,.023,"brass")]
        else:
            boxes += [(0,.413,0,.276,.218,.101,face), (0,.555,0,.213,.092,.085,edge),
                      (0,.621,0,.139,.042,.066,edge), (0,.413,.05,.074,.221,.039,edge),
                      (0,.357,0,.328,.043,.115,face), (0,-.366,0,.2,.048,.104,"wrap")]
        save(MODELS / "Tools" / f"{tier}_{tool}.glb", boxes)

# Short copper-and-amber sidearm. Side plates, grip scales and the vented
# collar sit proud of the frame, with dark gaps between the small blocks.
spark = [
    (0,.042,.106,.149,.119,.371,"navy"),
    (0,.122,.162,.167,.082,.432,"ink"),
    (0,.166,.15,.151,.026,.363,"steel"),
    (0,.094,.356,.132,.084,.194,"navy"),
    (0,.053,-.091,.12,.084,.048,"steel"),
    (0,-.102,.003,.112,.231,.119,"ink"),
    (0,-.179,-.017,.12,.066,.109,"navy"),
    (0,-.221,-.011,.13,.027,.121,"steel"),
    (0,-.103,.104,.057,.024,.134,"ink"),
    (0,-.057,.175,.057,.1,.024,"navy"),
    (0,-.044,.112,.025,.076,.025,"copper"),
    (0,.126,.45,.153,.107,.055,"copper"),
    (0,.129,.483,.139,.094,.021,"brass"),
    (0,.124,.489,.106,.064,.017,"charcoal"),
    (0,.124,.497,.048,.035,.013,"amber"),
    (0,.189,.387,.041,.019,.041,"ink"),
    (0,.202,.389,.02,.013,.021,"amber"),
    (0,.183,-.029,.1,.017,.055,"charcoal"),
    (0,.144,-.057,.087,.031,.009,"charcoal"),
]
for side in (-1, 1):
    spark += [
        (side*.08,.088,.196,.019,.045,.223,"copper"),
        (side*.086,.106,.2,.014,.012,.207,"brass"),
        (side*.068,.066,-.055,.007,.013,.019,"steel"),
        (side*.049,.191,-.03,.02,.029,.042,"ink"),
        (side*.043,.203,-.048,.01,.011,.012,"ceramic"),
        (side*.05,.158,-.064,.01,.009,.012,"amber"),
        (side*.059,-.117,.005,.02,.149,.089,"wood"),
    ]
    for z in (.225,.253,.281,.309):
        spark.append((side*.092,.123,z,.012,.023,.013,"charcoal"))
    for y in (-.168,-.143,-.118,-.093,-.068):
        spark.append((side*.071,y,.004,.01,.009,.072,"trim"))
    for z in (.093,.334):
        spark.append((side*.097,.089,z,.012,.022,.022,"amber"))
for z in (.041,.065,.089):
    spark.append((0,.187,z,.045,.012,.016,"amber"))
save(MODELS / "Tools" / "spark_pistol.glb", spark, WEAPON_COLORS)

# Long repeater with a pale heat shield over a recessed teal pulse cell.
# The top and the camera-facing rear get as much detail as the side panels.
repeater = [
    (0,.061,.129,.17,.135,.437,"navy"),
    (0,.133,.254,.151,.061,.67,"ink"),
    (0,.08,-.124,.105,.084,.18,"ink"),
    (0,.076,-.212,.121,.101,.034,"charcoal"),
    (0,.137,-.112,.121,.036,.111,"steel"),
    (0,-.098,.019,.121,.23,.125,"ink"),
    (0,-.214,.013,.132,.029,.135,"steel"),
    (0,-.099,.115,.068,.021,.113,"charcoal"),
    (0,-.065,.163,.068,.075,.023,"navy"),
    (0,-.027,.345,.146,.078,.249,"navy"),
    (0,-.06,.358,.126,.042,.171,"ink"),
    (0,.05,.434,.173,.105,.298,"jade"),
    (0,.062,.464,.124,.059,.198,"mint"),
    (0,.151,.56,.121,.088,.294,"steel"),
    (0,.126,.694,.155,.123,.093,"navy"),
    (0,.128,.75,.147,.112,.047,"ceramic"),
    (0,.126,.775,.104,.081,.026,"jade"),
    (0,.126,.788,.062,.046,.015,"charcoal"),
    (0,.126,.798,.03,.022,.013,"glow"),
    (0,.165,.185,.083,.015,.294,"jade"),
    (0,.191,-.052,.099,.031,.092,"navy"),
    (0,.208,-.067,.061,.013,.049,"jade"),
]
for side in (-1, 1):
    repeater += [
        (side*.091,.058,.038,.022,.076,.154,"ceramic"),
        (side*.104,.061,.039,.013,.042,.091,"navy"),
        (side*.075,.149,.535,.025,.059,.319,"ceramic"),
        (side*.076,.111,.553,.027,.016,.266,"jade"),
        (side*.089,.048,.441,.016,.024,.22,"glow"),
        (side*.09,.021,.451,.017,.018,.241,"navy"),
        (side*.046,.194,-.106,.028,.056,.034,"ink"),
        (side*.045,.217,-.121,.013,.016,.009,"mint"),
        (side*.06,-.111,.016,.02,.14,.093,"blue"),
        (side*.097,.07,.221,.012,.02,.026,"teal"),
    ]
    for z in (.421,.465,.509,.553,.597,.641):
        repeater.append((side*.093,.157,z,.016,.025,.016,"ink"))
    for y in (-.158,-.136,-.114,-.092,-.07):
        repeater.append((side*.073,y,.015,.01,.009,.083,"ink"))
    for z in (.207,.239,.271):
        repeater.append((side*.082,.143,z,.019,.02,.017,"mint"))
    for z in (-.018,.106):
        repeater.append((side*.112,.064,z,.01,.021,.02,"steel"))
for z in (.059,.096,.133,.17,.207,.244,.281):
    repeater.append((0,.177,z,.037,.012,.018,"mint" if z < .21 else "teal"))
for z in (.476,.515,.554,.593,.632):
    repeater.append((0,.201,z,.06,.015,.018,"ink"))
save(MODELS / "Tools" / "pulse_repeater.glb", repeater, WEAPON_COLORS)
