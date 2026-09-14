// Merges item_defs_2.01.00.tsv (index / key / internal name / loc keys, dumped from the running game
// with the research tool's "dumpdefs" mode) into item_names.json: every key the game knows but the
// old list does not gets an entry whose display name is derived from the internal name, flagged
// "derived": true so the UI can say the real name is unknown.
const fs = require('fs');
const path = require('path');
const dir = __dirname;
const json = JSON.parse(fs.readFileSync(path.join(dir, 'item_names.json'), 'utf8'));
const known = new Map(json.items.map(i => [i.itemKey, i]));
const rows = fs.readFileSync(path.join(dir, 'item_defs_2.01.00.tsv'), 'utf8').trim().split('\n').slice(1).map(l => l.split('\t'));

function derivedName(internal) {
  return internal
    .replace(/^(Optionary|Quest|Recipe|Book|CraftingRecipe|Item|Collection|Trade)_/, (m) => m.replace('_', ' ') )
    .replace(/_/g, ' ')
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim();
}
function category(internal) {
  if (/^(Quest_|NoticePaper|Book_|book_)/.test(internal)) return 'Quest';
  if (/(_Helm|_Armor|_Gloves|_Boots|_Cloak|Crown_Helm|Child(Boots|Helm|Gloves)|_Cloth$|_Suit$|_Mask$)/i.test(internal) && !/^(Recipe|CraftingRecipe|Trade)_/.test(internal)) return 'Equipment';
  if (/(Sword|Bow|Axe|Spear|Hammer|Shield|Dagger|Musket|Pistol|Rapier|Mace|Halberd|Flag|Necklace|Earring|Ring|BackPack)/i.test(internal)) return 'Misc';
  if (/(Bomb|Potion|Food_|Pill|Elixir|Drink)/i.test(internal)) return 'Consumable';
  if (/^Money_/.test(internal)) return 'Currency';
  return 'Misc';
}
let added = 0;
for (const [index, key, internal] of rows) {
  const k = +key;
  if (known.has(k) || !internal) continue;
  const cat = category(internal);
  json.items.push({ itemKey: k, name: derivedName(internal), category: cat, internalName: internal, maxStack: cat === 'Equipment' ? 1 : 100, derived: true });
  known.set(k, true);
  added++;
}
json.items.sort((a, b) => a.itemKey - b.itemKey);
fs.writeFileSync(path.join(dir, 'item_names.json'), JSON.stringify(json, null, 2));
console.log(`added ${added} derived entries; total ${json.items.length}`);
