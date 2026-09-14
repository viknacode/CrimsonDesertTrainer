// Builds armor_sets.json for the trainer from sets.tsv (vulkk catalog) + item_names.json.
const fs = require('fs');
const db = require(__dirname + '/item_names.json').items;
const rows = fs.readFileSync(__dirname + '/sets.tsv', 'utf8').trim().split('\n').map(l => l.split('\t'));

const slotOf = [
  ['head', /\b(Helm|Helmet|Hat|Hood|Mask|Cap|Crown|Circlet|Headgear|Bandana|Headband|Turban|Veil)\b/i],
  ['body', /\b(Armor|Attire|Robe|Robes|Coat|Tunic|Dress|Uniform|Garb|Outfit|Vest|Jacket|Suit|Mail)\b/i],
  ['hands', /\b(Gloves|Gauntlets|Bracers)\b/i],
  ['feet', /\b(Boots|Shoes|Greaves|Sandals)\b/i],
  ['back', /\b(Cloak|Cape|Mantle)\b/i],
];
const slotOrder = ['head', 'body', 'hands', 'feet', 'back'];
const excl = /\b(Blueprint|Recipe|Manual|Banner|Pike|Shield|Bow|Sword|Pack|Barding|Necklace|Key|Contract|Token|Lamp|Crystal)\b/i;
const demote = /(Gas Mask|Officer's|One-Armed|Lonely|Eclipsed|Captain's|Vice Captain)/i;
const exclInternal = /^(CraftingRecipe|Visione|Boss_Reward|Collection)/i;

// Search keys per set title (the catalog title, then aliases). First key that yields pieces wins
// unless "merge" is set, in which case pieces from every key are pooled.
const aliases = {
  'ASHCLAW (BLACK BEAR)': { keys: ['Ashclaw', 'Black Bear', "Black Bears'"], merge: true },
  'ASHEN (TARANDUS)': { keys: ['Tarandus'] },
  "ATOR'S WILL (ANTUMBRA)": { keys: ["Ator's Will"] },
  'BANDIT': { keys: ['Bandit Cloth'] },
  'CATFISH PIRATE': { keys: ['Sir Catfish', 'Catfish Plate', 'Dancing Catfish'], merge: true },
  'CIVILIAN DISGUISE': { keys: ['Disguise'] },
  'CONDEMNER (RIGHTEOUS INQUISITORS)': { keys: ["Condemner"] },
  'CRIMSON CHASER (CRIMSON WARDEN)': { keys: ['Crimson Chaser', "Crimson Warden's"], merge: true },
  'CURSED SOUL (FORTAIN, THE CURSED KNIGHT)': { keys: ['Cursed Soul'] },
  "DARK MARKSMAN'S PLATE (SAMARA, THE SANDWATCHER)": { keys: ["Dark Marksman's"] },
  'DEMENISSIAN GRAND GENERAL (LUCIAN BASTIER)': { keys: ['Bastier'] },
  'DEMENISSIAN SOLDIER': { keys: ['Demenissian Soldier Plate'] },
  'DESERT MARAUDER (HELMS)': { keys: ["Desert Marauder's"] },
  'FLAME KNIGHT SOLDIER': { keys: ["Flame Knight's"] },
  'FREESWORD': { keys: ["Wandering Freesword's", "Northern Freesword's"], merge: true },
  'GOLDEN FREE COMPANY (TWILIGHT MESSENGERS)': { keys: ["Golden Free Company's", "Twilight Messengers'"], merge: true },
  'HERNANDIAN BANQUET ATTIRE': { keys: ['Hernandian Banquet'] },
  'HERNANDIAN HONOR GUARD (BARDEN MIDDLER)': { keys: ['Hernandian Honor Guard'] },
  'IRONWILLED GUARDIAN (PAULUS)': { keys: ["Ironwilled Guardian's"] },
  'JACKALS': { keys: ["Jackals'"] },
  "MARTIAL MONK (T'RUKAN THE ASCENDED)": { keys: ['Trukan'] },
  'MUSKET BORDER GUARD STANDARD (DUSKSONGS)': { keys: ['Musket Border Guard Standard', 'Dusksongs'], merge: true },
  'NIBRAK CLOTH (GOLDLEAF SOLDIER)': { keys: ['Nibrak Cloth', 'Goldleaf Soldier'], merge: true },
  "ODECK'S PROTECTOR": { keys: ["Odeck's Protector"] },
  'PLATE ARMOR OF THE SHADOWS': { keys: ['of the Shadows'] },
  'PORORIN (SHAI ELDER)': { keys: ['Pororin'] },
  'SAHAZHAD PLATE (VARNIAN)': { keys: ['Sahazhad'] },
  'SCARLET BLADE (BLEED BANDITS)': { keys: ['Scarlet Blades'] },
  'SCHOLASTONE UNIFORM': { keys: ['Scholastone'] },
  'SCHTUMP (CALPHADEAN DEFENDER)': { keys: ['Schtump'] },
  'SUNSET REED (REED DEVIL)': { keys: ['Sunset Reed'] },
  'THE FACELESS (MASKED LIBERATORS)': { keys: ["The Faceless's"] },
  "THE MASKED LIBERATOR": { keys: ["The Masked Liberator's"] },
  'TIORANT (BLUE FANGS)': { keys: ['Tiorant'] },
  'UNYIELDING HERO (CASSIUS MORTEN)': { keys: ["Cassius Morten's"] },
  'UNYIELDING WARRIOR': { keys: ["Unyielding Warrior's"] },
  'WYVERNFLAMES': { keys: ['Wyvernflame'] },
  "YRKABEL (STAGLORD'S ACOLYTES)": { keys: ['Yrkabel'] },
  'FALLEN KINGDOM': { keys: ['of the Fallen Kingdom'] },
  'LOYAL FRIENDSHIP': { keys: ['of Loyal Friendship'] },
  "EARTH'S HONOR": { keys: ["of Earth's Honor"] },
  'ASHEN WOLF': { keys: ["Ashen Wolf's"] },
  'GREY WOLF': { keys: ['Grey Wolf Leather', 'Grey Wolf Cloth'], merge: true },
  'GREYMANE': { keys: ['Greymane', "Greymanes'", "Greymane's"], merge: true },
  'DELESYIAN GUARD': { keys: ["Delesyian Guard Captain's"] },
  'DRAGON FLAME (URDAVAHN)': { keys: ['Dragon Flame'] },
  'KNIGHT OF CARNAGE (GREGOR, HALBERD OF CARNAGE)': { keys: ['Knight of Carnage'] },
  'RIDING': { keys: ['Riding'] },
};

function norm(s) { return s.replace(/[\u2019]/g, "'").toLowerCase(); }
function escapeRe(s) { return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }
function titleCase(s) {
  return s.toLowerCase().replace(/(^|[\s(\-])([a-z])/g, (m, a, b) => a + b.toUpperCase()).replace(/\bT'rukan/i, "T'Rukan").replace(/\bOf\b/g, 'of').replace(/\bThe\b(?!$)/g, (m, off) => off === 0 ? 'The' : 'the').replace(/\bAnd\b/g, 'and');
}
function defaultKeys(title) {
  const m = title.match(/^(.*?)\s*(?:\((.*)\))?$/);
  const keys = [m[1].trim()];
  if (m[2]) for (const p of m[2].split(',')) keys.push(p.trim());
  return keys;
}
function matches(item, key) {
  const n = norm(item.name), k = norm(key);
  const re = new RegExp('(^|[^a-z])' + escapeRe(k) + '(?![a-z])', 'i');
  return re.test(n);
}
function slotFor(name) { let best = null, bestPos = -1; for (const [slot, re] of slotOf) { const m = name.match(re); if (m && m.index > bestPos) { best = slot; bestPos = m.index; } } return best; }
function variantRank(item) { return /_(?:[IVXLM]+|\d+)$/.test(item.internalName.trim()) ? 1 : 0; }

const sets = [];
for (const [grp, title, img, armorClass, resistance, abyss, source] of rows) {
  const cfg = aliases[title] || { keys: defaultKeys(title).map(k => k.replace(/^THE /i, '')) };
  const pool = [];
  for (const key of cfg.keys) {
    const found = db.filter(i => !excl.test(i.name) && !exclInternal.test(i.internalName) && slotFor(i.name) && matches(i, key))
      .map(i => ({ ...i, key, startsWith: norm(i.name).startsWith(norm(key)) || norm(i.name).endsWith(norm(key)) }));
    if (found.length && !cfg.merge) { pool.push(...found); break; }
    pool.push(...found);
  }
  const pieces = [];
  for (const slot of slotOrder) {
    const cands = pool.filter(i => slotFor(i.name) === slot);
    if (!cands.length) continue;
    cands.sort((a, b) => (demote.test(a.name) - demote.test(b.name)) || (b.startsWith - a.startsWith) || (variantRank(a) - variantRank(b)) || (a.itemKey - b.itemKey));
    const pick = cands[0];
    pieces.push({ slot, key: pick.itemKey, name: pick.name, variants: cands.length - 1 });
  }
  const m = title.match(/^(.*?)\s*(?:\((.*)\))?$/);
  sets.push({
    id: title.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, ''),
    name: titleCase(m[1].trim()),
    alias: m[2] ? titleCase(m[2]) : null,
    character: grp === 'D' ? 'Damiane' : 'Kliff & Oongka',
    armorClass, resistance, abyssGear: abyss, source,
    image: img.replace(/\.(jpe?g)$/i, '.jpg'),
    pieces,
  });
}
fs.writeFileSync(__dirname + '/armor_sets.json', JSON.stringify({ source: 'https://vulkk.com/2026/05/09/crimson-desert-armor-sets-catalog/', sets }, null, 1));
for (const s of sets) console.log(`${s.pieces.length}  ${s.name}${s.alias ? ' (' + s.alias + ')' : ''}: ${s.pieces.map(p => p.name + (p.variants ? '+' + p.variants : '')).join(' | ')}`);
console.log('sets', sets.length, 'with pieces', sets.filter(s => s.pieces.length).length, 'total pieces', sets.reduce((a, s) => a + s.pieces.length, 0));
