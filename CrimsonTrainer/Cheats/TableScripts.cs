using CrimsonTrainer.Memory;
using static Iced.Intel.AssemblerRegisters;

namespace CrimsonTrainer.Cheats;

/// <summary>
/// Ports of every auto-assembler script in CrimsonDesertv21.CT (MPElite / Tuuuup! / Austin /
/// bobdandy / supex0). Each factory reproduces the [ENABLE] half of a script: same AOB, same
/// freejumpmem slot, same cave code. Comments quote the original assembly.
/// </summary>
internal static class TableScripts
{
    private static AobPattern P(string text) => AobPattern.Parse(text);

    // ------------------------------------------------------------------
    // Current Player Pointers  (freejumpmem+10)  — captures cplayer / csplayer
    // ------------------------------------------------------------------
    public static Injection PlayerPointers(GameProcess game)
    {
        var hook = new HookSite
        {
            Name = "getcurrentplayer",
            Pattern = P("48xxxxxx48xxxxxxxxxxxx48xxxxxx0FB7xxxx66xxxxxxxxB8xxxxxxxx66xxxx74xx48xxxxxxxxE8xxxxxxxx0FB7xx48xxxxxxxx48xxxxB2xxFFxxxx0FB7xx48xxxxxxxxE8xxxxxxxx3A"),
            Length = 11, Slot = 0x10, Entry = "newmem",
        };
        return new Injection(game, "Player pointers", new[] { hook }, (b, inj) =>
        {
            var a = b.Asm;
            var cplayer = b.Var("cplayer");
            var csplayer = b.Var("csplayer");
            b.Entry("newmem");
            a.mov(__qword_ptr[cplayer], rbx);          // save player actor pointer
            a.db(hook.Original[..4]);                  // mov rax,[rbx+68]  (original, verbatim)
            a.mov(__qword_ptr[csplayer], rax);         // save stat component pointer
            a.db(hook.Original[4..]);                  // mov rcx,[rax+1A0] (original, verbatim — offset changes per patch)
            b.Return(hook);
        });
    }

    // ------------------------------------------------------------------
    // Max Health + Stamina + Spirit (Godmode)  (freejumpmem+C0)
    // ------------------------------------------------------------------
    public static Injection Godmode(GameProcess game)
    {
        var hook = new HookSite { Name = "StaminaInj", Pattern = P("48 89 5F 08 48 8B 5C 24 48"), Length = 9, Slot = 0xC0, Entry = "newmem" };
        return new Injection(game, "Godmode", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            var isPlayer = a.CreateLabel("isPlayer");
            var code = a.CreateLabel("code");
            b.Entry("newmem");
            a.cmp(__dword_ptr[rdi + 0x5A0], 19);       // Spirit stat id => this is the player
            a.je(isPlayer);
            a.jmp(code);

            a.Label(ref isPlayer);
            a.push(rax);
            a.mov(eax, 999_999_999);
            a.mov(__dword_ptr[rdi + 0x18], eax);       // Max Health
            a.mov(__dword_ptr[rdi + 0x528], eax);      // Max Stamina
            a.mov(__dword_ptr[rdi + 0x5B8], eax);      // Max Spirit
            a.mov(__dword_ptr[rdi + 0x08], eax);       // Current Health
            a.mov(__dword_ptr[rdi + 0x518], eax);      // Current Stamina
            a.mov(__dword_ptr[rdi + 0x5A8], eax);      // Current Spirit
            a.pop(rax);
            a.mov(rbx, __qword_ptr[rsp + 0x48]);
            b.Return(hook);

            a.Label(ref code);
            a.mov(__qword_ptr[rdi + 0x08], rbx);       // original
            a.mov(rbx, __qword_ptr[rsp + 0x48]);
            b.Return(hook);
        });
    }

    // ------------------------------------------------------------------
    // Inventory count hook (2.01.00)  (freejumpmem+30) — ONE cave for four table scripts.
    //
    //   In 2.01.00 every slot-count change goes through
    //       add rdx,r8            ; rdx = old count + delta
    //       mov [rbx+10],rdx      <- hook (rbx = slot: item id at +8, count at +10)
    //       mov rax,r11
    //       mov dword [r11],0
    //       add rsp,50 / pop rbx / ret
    //   so "Items Don't Decrease" (v1/v2), the gain multiplier (old: add [r8+rdi+10],rcx),
    //   the stack lock and the item swapper (old: sub [rdx+rax+10],rcx) all live here,
    //   selected by cave variables. rcx/r8/r9/r10 are dead after the hook (the function
    //   returns right away), so they can be used freely.
    // ------------------------------------------------------------------
    private const string ItemNoDecAob = "48 89 53 10 4C 89 D8 41 C7 03 00 00 00 00";

    public static Injection InventoryCount(GameProcess game)
    {
        var hook = new HookSite { Name = "ItemNoDec", Pattern = P(ItemNoDecAob), Length = 7, Slot = 0x30, Entry = "newmem" };
        return new Injection(game, "Inventory count hook", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            var noDecMode = b.Var("noDecMode");     // 0 off · 1 = v1 (never decrease) · 2 = v2 (only while count > 1)
            var gainAmount = b.Var("gainAmount");   // 0 off · N = every increment becomes +N
            var lockMode = b.Var("lockMode");       // 0 off · 1 = decrements rewrite the count to lockAmount (0 = old count)
            var lockAmount = b.Var("lockAmount");
            var swapMode = b.Var("swapMode");       // 0 off · 1 = decrements rewrite the slot to swapId × 1
            var swapId = b.Var("swapId");
            var lastEntry = b.Var("lastEntry");     // the inventory entry the game touched last (spawner target)
            var lastOld = b.Var("lastOld");         // its count before the change
            var lastNew = b.Var("lastNew");         // the count the game wanted to write
            var lastField8 = b.Var("lastField8");   // [entry+8] — the item reference
            var hits = b.Var("hits");               // how many times the hook ran

            var increment = a.CreateLabel("increment");
            var decrement = a.CreateLabel("decrement");
            var notSwap = a.CreateLabel("notSwap");
            var noLock = a.CreateLabel("noLock");
            var keepOld = a.CreateLabel("keepOld");
            var skipWrite = a.CreateLabel("skipWrite");
            var doWrite = a.CreateLabel("doWrite");

            b.Entry("newmem");
            a.mov(r8, __qword_ptr[rbx + 0x10]);        // old count
            // ---- capture for the spawner (rcx/r9/r10 are dead here) ----
            a.mov(__qword_ptr[lastEntry], rbx);
            a.mov(__qword_ptr[lastOld], r8);
            a.mov(__qword_ptr[lastNew], rdx);
            a.mov(rcx, __qword_ptr[rbx + 0x08]);
            a.mov(__qword_ptr[lastField8], rcx);
            a.inc(__qword_ptr[hits]);
            a.cmp(rdx, r8);
            a.jg(increment);
            a.jl(decrement);
            a.jmp(doWrite);

            // ---- count going up: gain multiplier ----
            a.Label(ref increment);
            a.mov(rcx, __qword_ptr[gainAmount]);
            a.test(rcx, rcx);
            a.jz(doWrite);
            a.lea(rdx, __[r8 + rcx]);                  // new = old + amount
            a.jmp(doWrite);

            // ---- count going down: swapper, then lock, then don't-decrease ----
            a.Label(ref decrement);
            a.cmp(__qword_ptr[swapMode], 0);
            a.je(notSwap);
            a.mov(rcx, __qword_ptr[swapId]);
            a.test(rcx, rcx);
            a.jz(notSwap);
            a.mov(__qword_ptr[rbx + 0x08], rcx);       // write target item id
            a.mov(rdx, 1);                             // count 1
            a.jmp(doWrite);

            a.Label(ref notSwap);
            a.cmp(__qword_ptr[lockMode], 0);
            a.je(noLock);
            a.mov(rcx, __qword_ptr[lockAmount]);
            a.test(rcx, rcx);
            a.jz(keepOld);
            a.mov(rdx, rcx);                           // lock to the chosen amount
            a.jmp(doWrite);
            a.Label(ref keepOld);
            a.mov(rdx, r8);                            // keep the old count
            a.jmp(doWrite);

            a.Label(ref noLock);
            a.mov(rcx, __qword_ptr[noDecMode]);
            a.test(rcx, rcx);
            a.jz(doWrite);                             // off -> normal decrement
            a.cmp(rcx, 2);
            a.jne(skipWrite);                          // v1: skip the write
            a.cmp(r8, 1);
            a.jle(doWrite);                            // v2: count <= 1 (equipment) -> allow

            a.Label(ref skipWrite);
            a.mov(rax, r11);                           // stolen bytes
            b.Return(hook);

            a.Label(ref doWrite);
            a.mov(__qword_ptr[rbx + 0x10], rdx);       // original write
            a.mov(rax, r11);                           // stolen bytes
            b.Return(hook);
        });
    }

    // ------------------------------------------------------------------
    // Hovered-slot reader  (freejumpmem+D0) — the "slots" hook of the Item Swapper, unchanged in 2.01.00.
    //   Entry layout (2.01.00): +8 = runtime item index (NOT the item key), +10 = count (int64), +A0 = flag byte.
    // ------------------------------------------------------------------
    public static Injection HoverReader(GameProcess game)
    {
        var slots = new HookSite { Name = "slots", Pattern = P("48 83 78 10 ? 7E ? 80 B8 A0 00 00 00"), Length = 5, Slot = 0xD0, Entry = "slotsCode" };
        return new Injection(game, "Hovered slot reader", new[] { slots }, (b, _) =>
        {
            var a = b.Asm;
            var selectedNum = b.Var("selectedNum");
            var invId = b.Var("invId");
            var slotPtr = b.Var("slotPtr");
            b.Entry("slotsCode");
            a.mov(__qword_ptr[slotPtr], rax);          // the inventory entry itself (for direct writes)
            a.mov(r10, __qword_ptr[rax + 0x10]);
            a.mov(__qword_ptr[selectedNum], r10);     // current item count in slot
            a.mov(r10, __qword_ptr[rax + 0x08]);
            a.mov(__qword_ptr[invId], r10);           // runtime item index in slot
            a.db(slots.Original);                      // cmp qword ptr [rax+10],imm8 (original)
            b.Return(slots);
        });
    }

    // ------------------------------------------------------------------
    // Gain 99999 Copper (Trigger: sell any item) — table: mov [rbx+D0],rcx; 2.01.00: mov [rbx+D8],rcx
    //   (unique match; the code before it is "rcx = [rcx+D8] + rdx", i.e. balance += amount)
    // ------------------------------------------------------------------
    public static Injection Copper99999(GameProcess game)
    {
        var hook = new HookSite { Name = "Money", Pattern = P("48 89 8B D8 00 00 00 48 8B BD"), Length = 7, Slot = 0x140, Entry = "newmem" };
        return new Injection(game, "Gain 99999 copper", new[] { hook }, (b, _) =>
        {
            b.Entry("newmem");
            b.Asm.mov(__dword_ptr[rbx + 0xD8], 99999);
            b.Return(hook);
        });
    }

    // ------------------------------------------------------------------
    // Max Trust - people / pets  (freejumpmem+50)
    //   table:   MaxTrust = "4E ?×12 20 C5 FC 11 49 20", hook at +0E = vmovups [rcx+20],ymm1
    //            replaced by "mov [rcx+20],#100" (the rest of that 32-byte copy was dropped).
    //   2.01.00: the trust-record upsert (CrimsonDesert.exe+D87BCB0, 0x12000 bytes before the
    //            horse-trust hook) looks the key up in a 0x68-stride array and, when found, copies
    //            the 0x68-byte record over the slot:
    //              add rcx,68 / cmp rcx,rdx / jne loop / jmp notFound
    //              vmovups ymm0,[rdi] / vmovups [rcx],ymm0
    //              vmovups ymm1,[rdi+20] / vmovups [rcx+20],ymm1   <- hook (+18, 5 bytes)
    //            Unique; two other 0x68-record copies exist (+1E2C20C, +1F8E267) but are unrelated
    //            containers. The record's trust value is the dword at +20, as in the table.
    // ------------------------------------------------------------------
    public static Injection MaxTrustPeople(GameProcess game)
    {
        var hook = new HookSite
        {
            Name = "MaxTrust",
            Pattern = P("48 83 C1 68 48 39 D1 75 ?? EB ?? C5 FC 10 07 C5 FC 11 01 C5 FC 10 4F 20 C5 FC 11 49 20"),
            Offset = 0x18, Length = 5, Slot = 0x50, Entry = "newmem",
        };
        return new Injection(game, "Max trust (people/pets)", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            b.Entry("newmem");
            a.vmovups(__ymmword_ptr[rcx + 0x20], ymm1); // original: copy record bytes +20..+3F
            a.mov(__dword_ptr[rcx + 0x20], 100);        // then trust := 100 (max)
            b.Return(hook);
        });
    }

    // ------------------------------------------------------------------
    // Max Trust - horse  (freejumpmem+60)
    // ------------------------------------------------------------------
    public static Injection MaxTrustHorse(GameProcess game)
    {
        var hook = new HookSite { Name = "HorseTrust", Pattern = P("49 89 47 08 41 89 36"), Length = 7, Slot = 0x60, Entry = "newmem" };
        return new Injection(game, "Max trust (horse)", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            b.Entry("newmem");
            a.mov(rax, 350);                           // horse max trust value
            a.mov(__qword_ptr[r15 + 0x08], rax);       // write to trust field
            a.mov(__dword_ptr[r14], esi);              // original secondary write
            b.Return(hook);
        });
    }

    // ------------------------------------------------------------------
    // Max Contribution  (freejumpmem+70)
    //   table: mov [rcx+10],rax / mov eax,[rsp+B8] / cmp edi,eax / jnl
    //   2.01.00: mov [rcx+10],rax / mov eax,[rsp+B8] / mov [rcx+0C],ebp / cmp edi,eax / jge  (unique)
    // ------------------------------------------------------------------
    public static Injection MaxContribution(GameProcess game)
    {
        var hook = new HookSite { Name = "MaxContribution", Pattern = P("48 89 41 10 8B 84 24 B8 00 00 00 89 69 0C 3B F8 7D"), Length = 11, Slot = 0x70, Entry = "newmem" };
        return new Injection(game, "Max contribution", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            b.Entry("newmem");
            a.mov(__dword_ptr[rcx + 0x10], 99999);     // override contribution total
            a.mov(eax, __dword_ptr[rsp + 0xB8]);       // original secondary instruction
            b.Return(hook);
        });
    }

    // ------------------------------------------------------------------
    // Max Resistance Stats + Attack & Defense  (freejumpmem+80)
    // ------------------------------------------------------------------
    public static Injection MaxStats(GameProcess game)
    {
        var hook = new HookSite { Name = "Stats", Pattern = P("4A 01 34 F1 48 03 D6"), Length = 7, Slot = 0x80, Entry = "newmem" };
        return new Injection(game, "Max resistance / attack / defense", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            b.Entry("newmem");
            a.mov(__dword_ptr[rcx + r14 * 8], 999_999_999); // override stat value with max
            a.add(rdx, rsi);                                 // original follow-up instruction
            b.Return(hook);
        });
    }

    // ------------------------------------------------------------------
    // Instant Horse Capture  (freejumpmem+90)
    //   table:   vaddss xmm2,xmm1,xmm2 / vucomiss xmm2,xmm2 / vmovss [rbx],xmm2 <- hook / setp al / cmp al,1
    //            cave: vmovss xmm2,[rbx+1C] / vmovss [rbx],xmm2   (progress := threshold)
    //   2.01.00: the gauge is a timer-driven "progress += elapsed × rate" method (+98CF890):
    //              vmulss xmm1,xmm0,[rdi+4]            ; delta = seconds × rate
    //              vaddss xmm6,xmm1,xmm6               ; progress + delta
    //              vmovaps xmm0,xmm6                   <- hook (+4, 8 bytes)
    //              vmovss [rdi],xmm6
    //              call fpclassify / cmp ax,2 / je     ; NaN check (was the inline vucomiss/setp)
    //              vminss xmm1,xmm6,[rdi+1C] / vmovss xmm0,[rdi+18] / vmaxss xmm1,xmm0,xmm1 / vmovss [rdi],xmm1
    //            Same layout: progress at +0, min at +18, max at +1C. The cave loads the threshold
    //            into xmm6 (callee-saved, so it survives the fpclassify call) so that both the store
    //            and the clamp that follows write the threshold.
    // ------------------------------------------------------------------
    public static Injection InstantHorseCapture(GameProcess game)
    {
        var hook = new HookSite { Name = "HorseWrite", Pattern = P("C5 F2 58 F6 C5 F8 28 C6 C5 FA 11 37 E8"), Offset = 4, Length = 8, Slot = 0x90, Entry = "newmem" };
        return new Injection(game, "Instant horse capture", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            b.Entry("newmem");
            a.vmovss(xmm6, __dword_ptr[rdi + 0x1C]);   // progress := max capture threshold
            a.vmovaps(xmm0, xmm6);                     // original
            a.vmovss(__dword_ptr[rdi], xmm6);          // original store (now the threshold)
            b.Return(hook);
        });
    }

    // ------------------------------------------------------------------
    // Wild West Archery / Shooting  (freejumpmem+A0)
    //   table:   mov eax,[rdi+14] / mov ecx,[rdi+10] / cmp eax,ecx / setae dl
    //   2.01.00: mov eax,[rsi+10] / cmp [rsi+14],eax / setae dl   (unique; the same compare is
    //            repeated after the hit counter "inc [rax+r8+14]" and the two results are compared —
    //            the win event fires when score (+14) crosses the target (+10)).
    //   Same trick as the table: score := 0, target := 1, so the next hit crosses the line.
    // ------------------------------------------------------------------
    public static Injection Archery(GameProcess game)
    {
        var hook = new HookSite { Name = "Archery", Pattern = P("8B 46 10 39 46 14 0F 93 C2"), Length = 6, Slot = 0xA0, Entry = "newmem" };
        return new Injection(game, "Wild West archery", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            b.Entry("newmem");
            a.mov(__dword_ptr[rsi + 0x14], 0);         // score -> 0
            a.mov(__dword_ptr[rsi + 0x10], 1);         // target -> 1 (first hit wins)
            a.mov(eax, __dword_ptr[rsi + 0x10]);       // original compare (flags feed the setae after return)
            a.cmp(__dword_ptr[rsi + 0x14], eax);
            b.Return(hook);
        });
    }

    // ------------------------------------------------------------------
    // Durability  (freejumpmem+B0 / +F0)
    //   table:   mov [rbp+40],ax / jns +0B
    //   2.01.00: movzx eax,di / add ax,r12w / mov [rsi+40],ax / jns +04   (only such site in the binary)
    //   The jns reads the flags of the preceding add, which the caves leave untouched.
    // ------------------------------------------------------------------
    private const string DurabilityAob = "66 89 46 40 79 ??";

    private static nint DurabilityJnsTarget(HookSite hook) => hook.Address + 6 + (sbyte)hook.Original[5];

    public static Injection Durability100(GameProcess game)
    {
        var hook = new HookSite { Name = "InfDurability", Pattern = P(DurabilityAob), Length = 6, Slot = 0xB0, Entry = "newmem" };
        return new Injection(game, "Durability 100", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            var notSigned = a.CreateLabel("notSigned");
            b.Entry("newmem");
            a.mov(__word_ptr[rsi + 0x40], 100);        // write 100 as a 16-bit word (matches the original ax write)
            a.jns(notSigned);                          // original jns
            b.Return(hook);
            a.Label(ref notSigned);
            b.JmpAbs((ulong)DurabilityJnsTarget(hook));
        });
    }

    // "Disable durability damage": skip the store but keep the branch exactly as the game had it.
    public static Injection DurabilityNoDamage(GameProcess game)
    {
        var hook = new HookSite { Name = "DisableDur", Pattern = P(DurabilityAob), Length = 6, Slot = 0xF0, Entry = "newmem" };
        return new Injection(game, "Durability no damage", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            var notSigned = a.CreateLabel("notSigned");
            b.Entry("newmem");
            a.jns(notSigned);                          // original jns, store skipped
            b.Return(hook);
            a.Label(ref notSigned);
            b.JmpAbs((ulong)DurabilityJnsTarget(hook));
        });
    }

    // ------------------------------------------------------------------
    // TimeScale Modifier  (freejumpmem+120 + 12-byte NOP patch)
    // ------------------------------------------------------------------
    public static Injection TimeScale(GameProcess game)
    {
        // table (1.04.02): [rbx+CD0] / [rbx+CD4] — 2.01.00: [rbx+CE0] / [rbx+CE4], surrounding code identical
        var hook = new HookSite { Name = "TimeScaleInj", Pattern = P("C5 FA 11 83 E0 0C 00 00"), Length = 8, Slot = 0x120, Entry = "newmem" };
        var divFix = new NopSite { Name = "TimeScaleDivFix", Pattern = P("C5 FA 5E C1 C5 FA 11 83 E4 0C 00 00"), Length = 12 };
        return new Injection(game, "TimeScale", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            var scale = b.Var("TimeScaleFloat", BitConverter.SingleToUInt32Bits(1.0f));
            b.Entry("newmem");
            a.mov(ecx, __dword_ptr[scale]);
            a.mov(__dword_ptr[rbx + 0xCE0], ecx);      // custom forward timescale
            a.mov(__dword_ptr[rbx + 0xCE4], ecx);      // custom inverse timescale (divss NOP-ed)
            b.Return(hook);
        }, new[] { divFix });
    }

    // ------------------------------------------------------------------
    // Always Perfect / Easy Parry  (freejumpmem+130) — marked BROKEN in the table
    // ------------------------------------------------------------------
    public static Injection EasyParry(GameProcess game)
    {
        var hook = new HookSite { Name = "EasyParryInj", Pattern = P("48 8B C4 55 41 56 48 81"), Length = 6, Slot = 0x130, Entry = "newmem" };
        return new Injection(game, "Easy parry", new[] { hook }, (b, _) =>
        {
            var a = b.Asm;
            b.Entry("newmem");
            a.mov(al, 1);                              // signal parry success
            a.ret();                                   // return directly to the game caller
        });
    }
}
