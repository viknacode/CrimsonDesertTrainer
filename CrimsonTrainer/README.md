# CrimsonTrainer v2 (C# / WPF)

Trainer com interface gráfica que porta os scripts da tabela Cheat Engine `CrimsonDesertv21.CT`
(MPElite · Tuuuup! · Austin · bobdandy · supex0) para código C#, mais extras construídos em cima da
cadeia de ponteiros do jogador, hotkeys globais configuráveis e um spawner com busca nos 6.022 itens
de `item_names.json`.

Dependências: .NET 10 + WPF + [Iced](https://github.com/icedland/iced) (assembler x64 gerenciado,
usado para montar as caves em vez de codificar bytes na mão).

## Recursos e estado no patch 2.01.00

A tabela v21 é anterior ao 2.01.00; só o "Items don't decrease" veio com AOB atualizado. Os demais
foram pesquisados na memória do jogo em execução (padrões relaxados + desmontagem do contexto).

| Seção | Recurso | Estado |
|---|---|---|
| Player | Player tracking (cplayer / csplayer) | funciona |
| Player | Stats ao vivo, Keep Health/Stamina/Spirit full, editar máximos / Attack / Defense | funciona (cadeia de ponteiros) |
| Player | Godmode | reimplementado por ponteiro: máximos → 999.999 + keep full, restaura ao desligar |
| Player | Max Attack & Defense + resistências | reimplementado por ponteiro (restaura ao desligar): Attack, Defense e qualquer atributo marcado **MAX** no editor dos 42 atributos de combate (a instrução de incremento que a tabela hookava não existe mais). As resistências ainda não têm índice conhecido — ver "Resistências" abaixo |
| Player | Max contribution | rebaseado (match único, mesma sequência de instruções) |
| Player | Max trust (people/pets) | rebaseado no *upsert* do registro de confiança (0x68 bytes; caminho "achou → copia por cima", match único), **não verificado in-game** |
| Player | Max trust (horse) | funciona (AOB original) |
| Player | Max trust — shop NPCs & novos conhecidos | novo (tabela mul0 1.00.04): os dois caminhos "registro novo" do mesmo upsert (append `+6F` e primeiro elemento `+B8` a partir da âncora do caminho "achou"), gravam 100 em +20, **não verificado in-game** |
| Player | Level & EXP editor | novo (mul0 "getData"): hook no getter de nível (`+C50FB40`, chamado de 10 lugares) captura o registro (`cData+8` = level, `+10` = EXP) e a tela mostra/edita os dois; abrir a tela de personagem dispara a captura, **não verificado in-game** |
| Inventory | Items don't decrease v1 / v2 | funciona (hook unificado) |
| Inventory | Gain multiplier ×9 / ×99 / ×99999 | rebaseado no hook unificado |
| Inventory | 99.999 copper ao vender | rebaseado (`[rbx+D0]` → `[rbx+D8]`, match único) |
| Inventory | Stack lock | rebaseado no hook unificado |
| Inventory | Unlimited money | novo (mul0): modo do hook unificado que pula todo decremento da entrada cujo índice runtime é o do cobre (item key 1, "Silver"/`Money_Copper`); o índice é escrito na cave quando a tabela runtime carrega |
| Inventory | Inventory slots (capacidade "239 / 240") | novo: acha os containers do inventário principal na heap (~2 s) e grava a capacidade (50–1.400; padrão 1.000) nas duas cópias que o jogo mantém; *Keep* reaplica a cada segundo. Ver "Slots do inventário" abaixo |
| Items | Grade do inventário + spawner | a tela mostra o inventário como grade de slots lida do jogo a cada 2 s (ícone do item vindo do crimsondb.gg — baixado uma vez e guardado em `%LocalAppData%CrimsonTrainericons` —, letra colorida por categoria quando não há ícone, badge com a contagem, abas por container, filtro); clicar num slot e escolher um item da lista grava índice runtime + contagem nas duas cópias do slot ("Put in slot") ou só a contagem ("Set count only"). Lista lida da **tabela runtime do jogo** (`[[CrimsonDesert.exe+6C2E2E8]+28]`, 6.813 itens); o slot guarda o **índice runtime**, não o itemKey. O fluxo antigo por hook (troca no próximo uso/drop) fica em "advanced" |
| Sets | Galeria de conjuntos de armadura | novo: os 103 conjuntos do catálogo do vulkk.com (foto, classe, resistência, região, personagem) com as peças resolvidas na lista de itens (99 conjuntos com peça, 362 peças); clicar num set e em *Spawn set* grava cada peça por cima de um item do inventário principal. Ver "Conjuntos de armadura" abaixo |
| Teleport | Posição, ir para coordenadas, waypoints, nudge | reescrito em cima do "getCoords" do mul0: o hook *Player transform* captura o transform do controlador (`[[[ator+18]+140]+2B8]`, posição em +90/+94/+98) — a cópia que a simulação obedece; as cópias das cadeias de ponteiro são gravadas junto. Antes só as cópias-espelho eram gravadas, por isso não teleportava. **Não verificado in-game** |
| World | Time scale | rebaseado (`CD0/CD4` → `CE0/CE4`, código ao redor idêntico) |
| World | Instant horse capture | rebaseado no integrador do medidor (progresso += tempo × taxa, min em +18 / máx em +1C como na tabela), **não verificado in-game** |
| World | Wild West archery | rebaseado (`mov eax,[rsi+10] / cmp [rsi+14],eax / setae dl`, match único; mesma semântica alvo/pontuação), **não verificado in-game** |
| World | Super movement speed | novo (mul0): no update do controlador (`[controller+2B8]` = transform: posição +90/+94/+98, velocidade +C0/+C4/+C8) empurra a posição por `velocidade × mult × 0,01` por frame (mult padrão 6), **não verificado in-game** |
| World | Super jump | novo (mul0): no mesmo transform, enquanto a velocidade vertical é positiva soma `boost` (padrão 0,2) ao Y por frame; a cláusula da flag de pulo (+1B4) do mul0 ficou de fora por não estar verificada no 2.01.00, **não verificado in-game** |
| World | Durabilidade 100 / sem dano | rebaseado, **não verificado in-game** |
| World | Perfect parry | AOB encontrado; marcado BROKEN na própria tabela |
| Character | Kliff / Damiane body & head scale | scan de memória inteira (como o Lua da tabela) |

Cheats cujo padrão não existe na versão instalada aparecem esmaecidos com o motivo. Cada cheat tem
**hotkey global** própria (clique em *Set key*, pressione a combinação; Esc cancela, Backspace limpa);
cheats com variantes ciclam entre as opções com a hotkey.

### Hook unificado de inventário

No 2.01.00 toda alteração de contagem de slot passa por `add rdx,r8 / mov [rbx+10],rdx`
(rbx = slot: id em +8, count em +10). Uma única cave nesse ponto (`TableScripts.InventoryCount`)
implementa don't-decrease, multiplicador, stack lock e swapper por variáveis de modo, então eles
podem ser combinados; o hook entra com a primeira feature ligada e sai com a última
(`SharedInjection` / `SharedFeature` em [Cheats/Cheat.cs](Cheats/Cheat.cs)).

### Slots do inventário

O "239 / 240" do inventário não é peso: é o container do inventário principal
([Cheats/InventorySlots.cs](Cheats/InventorySlots.cs)). Cada container é um objeto de 0x30 bytes:

```
+00 ponteiro para o pool de entradas   +08 entradas do pool (1460)   +0C idem
+10 tipo (int16, 1 = inventário)       +12 slots usados              +14 capacidade  ← "239 / 240"
+16 bônus de slots                     +18 bônus A                   +1A bônus B
```

capacidade = base (50 para o tipo 1) + bônus. O pool tem sempre 1.460 entradas de 0xC8 bytes
(as mesmas entradas que o hook de contagem recebe em `rbx`), por isso a capacidade pode subir até
~1.400 sem o jogo precisar realocar nada — é o motivo de outros trainers pararem em 1.000. O jogo
mantém duas cópias sincronizadas do inventário (a que o código de itens grava e a que a UI lê);
o trainer acha as duas por assinatura na heap do jogo (endereços ≥ 0x4'0000'0000, ~3 GB, ~2 s —
a memória abaixo disso é compartilhada com a GPU e lê a ~20 MB/s, só é varrida se nada for
encontrado) e grava capacidade + bônus nas duas, movendo o bônus pelo mesmo delta para que um
recálculo `base + bônus` caia no mesmo número.

### Resistências (Fire / Ice / Lightning)

O script "Max Resistance Stats" da tabela hookava `add [rcx+r14*8],rsi / add rdx,rsi` — o incremento
de um slot do array de 42 atributos de combate (`[[[cplayer+68]+20]+18]+38`, int64 × 1000) que roda
ao trocar de equipamento. No 2.01.00 essa instrução não existe em nenhuma forma reconhecível
(nenhum `add [base+idx*8],r64` seguido de `add r,r`, nem com deslocamento +38), então o trainer faz
o mesmo efeito por ponteiro: **Max Attack, Defense & selected attributes** segura em 999.999 os
índices 0 e 1 (Attack / Defense) e qualquer atributo marcado **MAX** no editor "Combat attributes".
Os índices das resistências ainda não são conhecidos — o binário tem as strings `FireResistance`,
`IceResistance` e `ElectricityResistance`, mas a ordem do array vem dos dados do jogo. Para achar:
abra o editor, equipe/desequipe uma peça com resistência a fogo e o índice que mudar acende
**CHANGED** por ~6 s; marque-o **MAX** (fica salvo em `settings.json`).

### Tabela do mul0 (Crimson Desert v1.00.04.CT)

Segunda fonte de scripts: a tabela do [mul0](https://mul0.com/) para a 1.00.04. O que ela tem que a v21
não tinha foi rebaseado para o 2.01.00 em [Cheats/TableScripts.cs](Cheats/TableScripts.cs)
(`MaxTrustNewRecords`, `LevelRecord`, `MoveSpeed`, `JumpHeight`) e no modo `moneyMode` do hook
unificado. Sites que ficam a um deslocamento fixo de uma âncora (os dois caminhos do trust) levam
`HookSite.Expect` com os bytes esperados, para uma mudança de layout ser recusada em vez de patchar a
instrução errada. O "Max Trust" dela já existia aqui (Max trust — people & pets); o "Teleport" dela
virou a base do nosso (hook `PlayerTransform`, ver a linha Teleport da tabela).

### Conjuntos de armadura (Sets)

`Resources/armor_sets.json` vem do [catálogo do vulkk.com](https://vulkk.com/2026/05/09/crimson-desert-armor-sets-catalog/)
(103 conjuntos: nome, apelido, classe, resistência, Abyss Gear, região, personagem, foto). O site fica
atrás do Cloudflare, então a extração foi feita pelo navegador do usuário; `Resources/sets.tsv` é o
que saiu do catálogo e `Resources/build-sets.js` (Node) gera o JSON a partir dele: as peças de cada conjunto são procuradas em `item_names.json` pelo nome
do set (com apelidos manuais — "Ashclaw (Black Bear)" → "Black Bears' …", "Martial Monk" → "Trukan …",
"Fallen Kingdom" → "… of the Fallen Kingdom"), uma peça por slot (Helm / Armor / Gloves / Boots / Cloak),
excluindo blueprints, armas e estandartes e preferindo o nome que começa pelo set e sem sufixo de
variante. 99 conjuntos têm peças; Baltheon, Delesyian Military, Knight of Carnage e Tommaso Guard não
existem na lista de itens (1.13+). As fotos estão em `Resources/sets/*.jpg` (248×372, ~2,5 MB no
total, embutidas como `Resource`).

Como o trainer não cria entradas novas no pool do inventário (os campos internos de uma entrada
— id de instância, tags, timestamps — não são conhecidos o bastante para sintetizar uma), *Spawn set*
faz o mesmo que "Put in slot" para cada peça (o painel ao lado da galeria lista as peças; cada uma pode ser
desmarcada e, quando a lista de itens tem variantes com o mesmo nome — versão do jogador vs. versão de NPC —,
escolhida entre elas): escolhe as N entradas do inventário principal com o
maior valor em `+90` (hora de criação, ou seja, os itens pegos mais recentemente), nunca uma que já
seja peça do set, mostra antes a lista "WILL REPLACE" (slot, item e quantidade que somem) e grava
índice runtime + contagem 1 nas duas cópias do container. Dica: pegue N itens de lixo antes de
spawnar. A prévia é refeita a cada 2 s enquanto a tela está aberta e de novo no clique.

### Ícones dos itens

Os ícones ficam dentro dos pacotes de assets criptografados do jogo, então vêm da base
comunitária [crimsondb.gg](https://crimsondb.gg): `Resources/icon_index.json` (gerado pelo
`crawl-icons.ps1` do scratchpad a partir das listas por categoria do site) mapeia nome em inglês →
caminho do ícone, e [Items/IconCache.cs](Items/IconCache.cs) baixa cada um sob demanda como PNG
64×64 pelo proxy de imagens do site (`/_ipx/f_png&s_64x64/images/items/<categoria>/<hash>.webp`),
guardando em `%LocalAppData%\CrimsonTrainer\icons`. Cobre ~3.800 dos 6.022 nomes; sem rede ou sem
ícone, o tile mostra a letra colorida por categoria.

## Como funciona

Cada script vira uma `Injection` ([Cheats/Injection.cs](Cheats/Injection.cs)): os mesmos slots em
`freejumpmem = módulo+0x500`, a mesma estrutura de cave (montada com Iced a partir de
[Cheats/TableScripts.cs](Cheats/TableScripts.cs), com o assembly original nos comentários).

| Script CE | Equivalente |
|---|---|
| `aobscanmodule` | `AobPattern.ScanBytes` sobre uma leitura única do módulo |
| `alloc(newmem)` / `dealloc` | `VirtualAllocEx` / `VirtualFreeEx` |
| `fullaccess(freejumpmem,$1000)` | `VirtualProtectEx` RWX |
| `freejumpmem+XX: jmp newmem` | `FF 25 + endereço` (jump absoluto) |
| `hook: jmp freejumpmem+XX / nop` | `E9 rel32 + 90…` com threads suspensas |
| `label: dd 0` (swapId, TimeScaleFloat…) | variáveis qword dentro da cave (`ReadVar` / `WriteVar`) |
| `[DISABLE]` | restaura os bytes originais e libera a cave |

Diferenças deliberadas em relação à tabela:
- Antes de aplicar um hook o trainer confere que a instrução ainda tem os bytes originais — se outro
  cheat (ou a tabela CE) já a patchou, recusa e explica no log.
- "Max Trust (people)" mantém a cópia original dos bytes +20..+3F do registro e só depois grava 100 em +20 (a tabela pulava a cópia inteira).
- "Instant horse capture" carrega o limite em xmm6 (preservado pela chamada de fpclassify) em vez de só gravar em [rdi], porque o clamp que vem logo depois regravaria o progresso antigo.
- "Durability — no damage" pula só o store e mantém o `jns` original (a tabela pulava os dois).
- No leitor de hover o `cmp` original é reexecutado com o imediato real lido do jogo.
- v1 do don't-decrease só ignora decrementos (a tabela ignorava também incrementos).

## Compilar

```bash
dotnet build CrimsonTrainer -c Release
```

Único `.exe` autocontido:

```bash
dotnet publish CrimsonTrainer -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Se `OpenProcess` falhar (erro 5), execute como Administrador. `--pid N` anexa só a esse processo.
Não deixe a tabela CE ativa ao mesmo tempo. As preferências (hotkeys e valores) ficam em
`%LocalAppData%\CrimsonTrainer\settings.json`.

## Estrutura

```
CrimsonTrainer/
  App.xaml, MainWindow.xaml(.cs)      janela (rail de navegação, seções, barra de status, captura de hotkey)
  Themes/Theme.xaml                   paleta e estilos (switch, segmentado, chips, textbox, listbox…)
  Views/CheatTemplates.xaml           templates dos cards de cheat, campos e hotkeys
  ViewModels/                         MainViewModel (sessão), CheatViewModel, Player/Spawner/InventoryGrid/InventorySlots/Sets/BodyScale
  Cheats/Injection.cs                 cave + hooks + slots + variáveis (Iced)
  Cheats/TableScripts.cs              scripts (originais da tabela e rebaseados para 2.01.00)
  Cheats/Cheat.cs, CheatCatalog.cs    modelo (toggle / choice / feature compartilhada) e catálogo
  Cheats/PlayerStats.cs, BodyScale.cs cadeia de ponteiros e escala corporal
  Items/ItemDatabase.cs               busca nos itens (JSON embutido em Resources/)
  Items/InventoryScanner.cs           containers (header 0x30) e entradas (0xC8) do inventário do jogador
  Items/ArmorSetCatalog.cs            conjuntos de armadura (armor_sets.json embutido) + Resources/sets/*.jpg
  Hotkeys/                            RegisterHotKey + captura
  Settings/AppSettings.cs             persistência
  Memory/, Native/                    processo, AOB, P/Invoke
```
