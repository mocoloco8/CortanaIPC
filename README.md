# CortanaIPC

A .NET library for building apps on top of the **cortana** Windower 4 addon for Final Fantasy XI
(`WindowerLuas/cortana`). It is the same link that CortanaXIHealer uses. It handles:

- the UDP link to each character's addon (handshake, heartbeats, latency probe, link loss);
- parsing everything the addon streams: player, party, nearby entities, recasts, buffs, inventory, chat, combat, NPC dialogs;
- typed commands back to the game: movement, targeting, chat and commands, keys, follow, job change, NPC selling and exchange replay.

You don't need EliteAPI, game-memory reads or Windower knowledge. Load the addon, reference the DLL, and read state or send commands.

| | |
|---|---|
| Targets | `netstandard2.0` (.NET Framework 4.6.1+, Mono, Unity) and `net10.0` |
| Namespace | `Cortana` |
| Dependencies | none |
| Transport | UDP on `127.0.0.1`, the first free port in 59332–59341 (several apps can be linked at once) |

## Quick start

1. In each FFXI client, run `//lua load cortana` (copy `WindowerLuas/cortana` into `Windower4/addons/` first).
2. Reference `CortanaIPC.dll` (or the `CortanaIPC.csproj` project).

To build the library on its own, open `CortanaIPC.sln` or run `dotnet build CortanaIPC.sln -c Release`. The output goes to `bin\Release\netstandard2.0\` and `bin\Release\net10.0\`.

```csharp
using Cortana;

var ipc = new CortanaIPC { AppName = "MyApp" };   // first free port in 59332-59341
ipc.CharacterLinked += c => Console.WriteLine(c.Name + " linked");
ipc.ChatReceived    += (c, line) => Console.WriteLine("[" + c.Name + "] " + line.Text);
ipc.StateChanged    += (c, what) =>
{
    if ((what & StateChanges.Vitals) != 0)
        Console.WriteLine(c.Name + " HP " + c.State.Player.Hpp + "%");
};
ipc.Start();

// ... later, from any thread:
var me = ipc.GetCharacter("Mychar");
if (me.IsLinked && me.State.IsFresh)
{
    var p = me.State.Player;
    Console.WriteLine(p.Hp + "/" + p.Mp + " TP " + p.Tp + " in zone " + p.Zone);

    foreach (var e in me.State.Entities())
        if (e.IsMonster && e.Distance < 20) Console.WriteLine(e.Name + " " + e.Hpp + "%");

    me.Chat("/ma \"Cure\" <me>");           // anything you could type, including //windower commands
}

ipc.Dispose();
```

## Threading model

- `Start()` creates two background threads. The receive thread parses messages and raises events. The probe thread sends latency probes and detects characters that have gone quiet.
- **Events fire on the receive thread, in arrival order.** Keep handlers short and hand heavy work to your own thread. In a UI app, marshal to the UI thread (`Control.BeginInvoke`, `Dispatcher.BeginInvoke`).
- A handler that throws is isolated: the exception goes to `HandlerError(where, exception)`, and the link and other handlers keep running.
- Reading `State` and calling commands is safe from any thread. State objects are immutable snapshots; each update replaces them, so a reference you hold never changes underneath you.
- While a character is unlinked, commands do nothing.

## Coordinates and ids

- Positions use **Windower's axes**: `X`/`Y` is the ground plane and `Z` is height. Headings are radians, as Windower's `turn` uses them (`CortanaCharacter.HeadingTo(x, y, tx, ty)` computes one).
- Entity **index** (0–4095, the client's array slot) and server **id** are distinct values. Commands take the index. `State.FindEntityById(id)` maps an id back to its entity.
- Status, job, zone, spell, ability and item ids are the game's own, the same as Windower's `res/`. Look them up with `ipc.Resources` (see below).

## API overview

### `CortanaIPC`: the link

| Member | |
|---|---|
| `new CortanaIPC()` | Binds the first free port in 59332–59341 when started. |
| `new CortanaIPC(int port)` | Binds exactly that port. `Start()` throws if it's taken. |
| `Port` | The bound port. It is also this app's identity to the addon (see [Several apps at once](#several-apps-at-once)). |
| `AppName` | Shown in game (`//cortana status` and link messages). Defaults to the process name. Set before `Start()`. |
| `BindAddress` | Default `IPAddress.Loopback`. Set before `Start()` to accept addons on other machines. |
| `LinkTimeout`, `ProbeInterval` | Defaults 6s and 2s. |
| `Start()` / `Stop()` / `Dispose()` | `Stop` tells every addon the app is leaving (`BYE`). |
| `Characters`, `LinkedCharacters` | Every character seen, and those currently linked. |
| `GetCharacter(name)`, `TryGetCharacter(name, out c)` | Names are case-insensitive. `GetCharacter` creates the character if it doesn't exist yet, so you can hold it before the addon connects. |
| `Send(name, raw)`, `Broadcast(raw)` | Raw protocol lines (see the protocol section below). |
| `On(type, handler)` / `Off(type, handler)` | Receive any message type as raw fields, including types this library doesn't model. |
| `Resources` | `WindowerResources` for the Windower install that the first addon reported (null until then). |
| `TryGetPartyBuffs(member, out ids, out at)` | A party member's buff ids (packet 0x076). |

**Events.** Each event's first argument is the `CortanaCharacter`.

| Group | Events |
|---|---|
| Link | `CharacterLinked`, `CharacterUnlinked`, `MessageReceived` (every raw message), `HandlerError` |
| State | `StateChanged(c, StateChanges)`, `ChatReceived`, `BuffTimersUpdated`, `PartyBuffsUpdated(member, now, before)`, `InventoryCountsUpdated`, `CurrencyUpdated`, `TypingChanged`, `CapabilitiesChanged`, `ExtrasUpdated` |
| Combat | `CastResolved` (our own action's outcome and amount), `ActionMessageReceived`, `CombatReported`, `SkillchainDetected`, `MobActionStarted`, `MobActionResolved`, `PlayerActionReported`, `CastReported` (own spell or item: `Begin`, `Interrupt`, `Finish`), `DebuffWoreOff`, `JobAbilityResolved`, `AutoTargetReported`, `HoverReported`, `EngagementDesync` |
| Loot / scouting | `TreasureFound`, `TreasureResolved`, `WidescanCompleted`, `EquipmentChanged` |
| NPC | `DialogOpened`, `DialogFailed`, `DialogChoiceSent`, `NpcEventStarted`, `NpcEventUpdated`, `ExchangeRecorded`, `SellCompleted`, `ReplayCompleted` |
| Misc | `CommandReceived` (`//cortana <feature> <action> [target]` typed in game), `JobChangeSent`, `JobChangeFailed` |

`StateChanged` fires only for **decision-relevant** changes, which are the `StateChanges` flags:

- `Vitals`
- `Status`
- `Buffs`
- `Target`
- `Zone`
- `Login`
- `Casting` (start and end)
- `TpReady` (TP crossed 1000)
- `Tp` (TP moved at all — own, a party member's, or the pet's; fires on every change so a TP readout can follow the game live)
- `Party` (membership or a member's HP; a member's TP raises `Tp`, not this)
- `RecastReady`
- `Pet` (summoned or released; HP%, MP% or target changed; TP crossed 1000)

Position and cast progress deliberately do **not** raise it, so it is safe to wake a decision loop on. Read positions from `State` when you need them.

### `CortanaCharacter`: one character

**Identity and link:**

- `Name`, `ServerId`, `AddonVersion`, `WindowerPath`
- `IsLinked`, `LastSeen`
- `Latency`: `LastMs`, `AverageMs`, `PeakMs`, `Samples`, measured by the ECHO round trip.

**Live data:**

- `State` (next section)
- `BuffTimers`: own buffs with `RemainingSeconds`. A permanent buff reports about 60000s.
- `InventoryCounts`: item id → count. Refresh it with `RequestInventory()`.
- `Currency`: `Sparks`, `Accolades`.
- `Extras`: `PlayerExtras` with `SpentJobPoints(jobId)`, `BaseStats` / `AddedStats` / `TotalStats` (STR…CHR), `Skill("healing_magic")` and the rest of Windower's skill keys, `TemporaryItemCount(itemId)`. Null until the first `SPX`, about a second after linking.
- `IsTyping`: the chat line is open, so don't send keys.
- Capabilities: `KeysConfirmed`, `KeysUnsupported`, `FollowConfirmed`, `FollowUnsupported`.

**NPC dialogs:**

- `LastDialogMenuId`, `LastDialogChoice`, `LastRecordedExchange`
- `DialogOpenedSince(tick, out menu)`, `DialogChoiceSince(tick, out choice)`

**Commands** (fire-and-forget unless noted):

| Command | Wire |
|---|---|
| `Run(heading)`, `StopRunning()` | `RUN` is a lease that the addon stops after 2s. Call `Run` again at least once a second while moving. |
| `Turn(heading)` | `TURN` |
| `KeyPress(key, holdMs)`, `KeyDown`, `KeyUp` | `KEYRAW` (Windower `setkey` names) |
| `MenuKey(key)`, `MenuKeySequence(keys)` | `KEY`, `KEYSEQ` (menu navigation) |
| `Chat(text)` | `CHATIN`. Any chat line or `/command`; `//cmd` runs a Windower command. |
| `SetTarget(index)` | `SMTARGET`: moves the reticle only (client side). |
| `EngageOrSwitch(index)` | `TARGET`: engages when idle, switches target when engaged (server-notified). |
| `Disengage()`, `AcceptRaise()` | |
| `Follow(index)`, `StopFollow()` | Windower's native follow. |
| `ChangeJob(main, sub)` | 0 keeps the current job. Must be near a Moogle. |
| `Notify(text)`, `Banner(text)`, `Hud(l1, l2)` | Chat-log echo, large on-screen banner, two-line HUD. |
| `PokeNpc(index, name)` | Talk to or open an NPC. |
| `Sell(npcIndex, itemIds, timeoutMs)` | **Blocks.** Sells every stack of those items; returns `SellResult { StacksSold, Error, Success }`. |
| `NpcReplay(npcIndex, menuId, choices, timeoutMs)` | **Blocks.** Replays a recorded exchange (below); returns `ReplayResult`. |
| `QueryCurrency(timeoutMs)` | **Blocks.** Fresh sparks and accolades from the server, or null. |
| `RequestCurrency()`, `RequestInventory()` | Non-blocking; the answers arrive as `CurrencyUpdated` / `InventoryCountsUpdated`. |
| `Lot(slot)`, `Pass(slot)`, `LotAll()`, `PassAll()` | Treasure pool. `LotAll`/`PassAll` cover every slot currently in `TreasurePool`. |
| `Widescan()` | Zone-wide mob sweep; results arrive as `WidescanMobs`, and `WidescanCompleted` fires once they stop coming. |

### `CharacterState`: the snapshot

| Member | |
|---|---|
| `Player` | `PlayerState`: `Hp`, `Hpp`, `Mp`, `Mpp`, `MaxMp`, `Tp`, `X`/`Y`/`Z`, `Heading`, `Status`, `Zone`, jobs and levels, `Login`, `CastProgress`, `IsCasting`, `PetIndex`, `TargetIndex`, `SubTargetIndex`, `TargetLocked`, `Buffs`, `HasBuff(id)`, `EventMenuId`, `ReceivedAt` |
| `Pet` | `PetState` for your own pet (never null): `Exists`, `Index`, `Name`, `Hpp`, `Mpp`, `Tp`, `TargetId`, `HasVitals`, `ReceivedAt`. HP% is always live. MP% and TP come from the server's pet status packets and read -1 (`HasVitals` false) until the first one arrives, which is usually as soon as the pet acts or its vitals change. Position and the rest are on the pet's entity: `GetEntity(Pet.Index)`. |
| `Party` | `IReadOnlyList<PartyMemberState>` — the members the addon reported, each with its `Slot` (0–5 party, 6+ alliance) |
| `Entities()`, `GetEntity(index)`, `FindEntityById(id)` | `EntityState`: `Name`, `Hpp`, position, `Distance`, `Status`, `ClaimId`, `SpawnType`, `IsMonster`, `IsPlayerCharacter`, `ValidTarget`, `TargetIndex`, `PetIndex`, `ModelSize`, `Race` |
| `Recasts` | `AbilityRecast(recastId)`, `SpellRecast(spellId)`, in seconds |
| `Inventory` | Main bag by slot: `Capacity`, `Slots`, `CountOf(itemId)` |
| `KnownSpells`, `KnowsSpell(id)` | |
| `KnownAbilities`, `KnowsAbility(id)` | Job abilities by Windower id (`res/job_abilities`) |
| `IsFresh`, `FeedAge`, `Login` | The addon sends on change plus heartbeats. A feed older than about 2s reads as `Loading`, so a dropped link looks like zoning. |
| `TryReadChat(out line)`, `WaitForChat(ms)` | Chat queue (1000 lines). `WaitForChat` wakes as soon as a line arrives. |

Entities are tracked incrementally. The addon sends changes, sends `SEX` when an entity leaves, and sends a full resync every 3s. An entity missing from `GetEntity` has despawned or is out of range.

### `WindowerResources`: game data

`ipc.Resources` reads the Windower install's `res/*.lua` files. You can also create one yourself with `new WindowerResources(@"C:\Windower4")`.

- `GetSpell(id)` / `FindSpell(name)` return `SpellInfo`: MP cost, cast time, recast, element, skill, targets, range, levels.
- `GetJobAbility(id)`, `GetWeaponSkill(id)` and `FindAbility(name)` return `AbilityInfo`: recast id, MP and TP cost, element, targets, range.
- `GetItem(id)` / `FindItem(name)` return `ItemInfo`: stack size, flags, type, level, cast time, charges.

Each file is loaded on first use.

## Several apps at once

Each app binds its own port in 59332–59341. The addon looks for apps on every port in that range, and every app that answers gets the full feed: state, chat, combat and NPC events. Up to ten apps can be linked to the same characters at the same time, for example CortanaXIHealer and EasyFarm.

The apps run independently, like any two programs:

- **Every app can send every command.** The addon doesn't arbitrate. If two apps drive the same character at once (both running it, or both targeting), the addon carries out whichever command arrived last.
- **The addon is the only source of character state.** An app never changes `State` itself. It sees the result of its own commands, and of every other app's, when the addon reports the change. Read `State` rather than assuming a command took effect.
- **Each app gets its own copy of everything.** That includes its own chat queue, events and latency probe. When an app links, the addon resends its complete state.

In game, `//cortana status` lists the linked apps.

## Recording and replaying NPC exchanges

Windower can't read NPC menus from memory, so exchanges are recorded once and replayed from packets. This is the capetrader technique.

1. Do the exchange once by hand with the addon loaded. The library records every choice the client sends (0x05B). When the event ends, it raises `ExchangeRecorded` with a `RecordedExchange` (`NpcIndex`, `MenuId`, `ReplayChoices`).
2. Save `ReplayChoices`, a string like `"1:0:0,2:0:0"`.
3. To replay, stand next to the NPC and call `me.NpcReplay(npcIndex, menuId, replayChoices, 15000)`. The addon opens the event, injects the same choices with the menu hidden, and reports back.

Option values are menu-specific encodings, not list positions. Replay them exactly as recorded.

## Wire protocol

This section is for implementing your own client or extending the addon. UTF-8 text datagrams, one message each, fields separated by `|`. For messages from the addon, field 1 is always the character name.

**Handshake.** Every 2s the addon sends `HELLO|name|serverId|zone|version|state1|windowerPath` to each port in 59332–59341 it isn't linked to. An app answers `WELCOME|cortana|appName` and becomes a subscriber: from then on it gets every feed message, and the addon resends its full state. The addon sends `PING|name` heartbeats to each subscriber, answered with `PONG`, and drops a subscriber it hasn't heard from in 6s. An app leaving sends `BYE`. The app measures latency with `ECHO|<stopwatch ticks>`, which the addon echoes back to that app only as `ECHOR|name|<ticks>`. The app's port is its identity, so it must send from the port it bound.

### Addon → app

| Message | Fields |
|---|---|
| `SPL` | `name\|id\|index\|hp\|hpp\|mp\|mpp\|maxmp\|tp\|x\|y\|z\|h\|status\|zone\|mjob\|mlvl\|sjob\|slvl\|login\|castpct\|petidx\|tidx\|stidx\|locked\|buffs\|menu` (buffs comma-separated) |
| `SEX` | `name\|index,index,…`: entities that left |
| `SPT` | `name\|rec;rec`, where each rec is `slot,name,id,index,hp,hpp,mp,mpp,tp,zone,mjob,mlvl,sjob,slvl` |
| `PET` | `name\|index\|hpp\|mpp\|tp\|targetId\|petName`: own pet, sent on change plus a 1s heartbeat. `index` 0 = no pet; `mpp`/`tp` -1 = not reported yet (from incoming 0x067/0x068). |
| `CASTRES` | `name\|cat=;id=;target=;targets=;msg=;param=;sum=;name=`: one of OUR actions resolved (0x028). `cat` 2 ranged, 3 weapon skill, 4 spell, 5 item, 6 job ability; `msg` is the action-message id, `param` the damage / cure / status it produced. |
| `AMSG` | `name\|msg=;actor=;target=;aindex=;tindex=;p1=;p2=`: an action message (0x029) our party is part of. |
| `TPOOL` | `name\|slot=;item=;count=;dropper=;old=;name=`: an item entered the treasure pool (0x0D2). |
| `TLOTR` | `name\|slot=;drop=;lot=;winner=;lotter=;lotted=`: a pool slot was lotted or resolved (0x0D3). `drop` 0 pooled, 1 won, 3 floored; `lotted` 65535 = passed. |
| `WSMOB` | `name\|index=;level=;type=;x=;y=;name=`: one mob from a widescan sweep (0x0F4). `x`/`y` are map offsets, not world coordinates. |
| `EQP` | `name\|main=;mainname=;mainskill=;sub=;subname=;range=;rangename=;rangeskill=;ammo=;ammoname=;ammocount=`: equipped weapons and ammo, sent on change plus a 10s heartbeat. |
| `SRC` | `name\|recastId=secs,…\|spellId=frames,…` (spell recasts in 1/60 s) |
| `SSP` | `name\|spellId,…`: known spells |
| `SAB` | `name\|abilityId,…`: known job abilities (sent with `SSP`) |
| `SPX` | `name\|jobId=spentJp,…\|str,dex,vit,agi,int,mnd,chr\|addedStr,…,addedChr\|skill=level,…\|itemId:count,…`: job points, base and added stats (from the last 0x061), skills, and the temporary-items bag. Sent on change, checked every 1s, heartbeat 10s |
| `CAST` | `name\|begin/interrupt/finish\|spell/item\|id`: own spell cast or item use (action categories 8/4 and 9/5) |
| `SIV` | `name\|max\|slot:itemId:count,…` |
| `SCH` | `name\|mode\|text` (the text may contain `\|`) |
| `BUFFS` | `name\|id:remSec,…` (an id without `:` is permanent) |
| `PBUFF` | `memberName\|id,…`: **field 1 is the party member**, not the sender |
| `INV` | `name\|slot:id:count,…` (the reply to `GETINV`) |
| `CUR` | `name\|sparks\|accolades` (-1 means not included in this packet) |
| `COMBAT` | `name\|key=value;…` (`type=SC` means a skillchain: `sc`, `target`, `src`) |
| `MOBACT` | `name\|cat=;id=;actor=;name=`: a mob starts a TP move or spell |
| `MOBRES` | `name\|cat=;id=;actor=;name=;targets=;dmg=` |
| `PLRACT` | `name\|cat=;id=;actor=;target=;name=` |
| `DEBUFFWEAR` | `name\|target=;tindex=;buff=;msg=;name=` |
| `ROLL` | `name\|abilityId\|value\|abilityName`: own job ability resolved |
| `AUTOTARGET` | `name\|serverId\|index\|dist` |
| `HOVER` | `name\|kind=shot\|ws;particle=0/1` |
| `CMD` | `name\|feature\|action\|target` |
| `TYPING` | `name\|0/1` |
| `KEYACK` / `KEYNAK`, `FOLLOWACK` / `FOLLOWNAK` | Capability confirmations |
| `DLGOPEN` / `DLGFAIL` | `name\|menuId`, `name\|reason` |
| `DLGOPT` | `name\|menuId\|option\|unknown1\|automated` |
| `EVT` / `EVTUPD` | `name\|packetId\|menuId\|npcId\|npcIndex\|paramsHex`, `name\|paramsHex` |
| `SELLDONE` / `REPLAYDONE` | `name\|sold\|reason`, `name\|ok\|reason` |
| `DESYNC`, `JOBSENT` (`\|main\|sub`), `JOBFAIL` (`\|reason`) | |

### App → addon

| Message | Fields |
|---|---|
| `WELCOME`, `BYE` | `WELCOME\|cortana\|appName` (answer to `HELLO`); `BYE` = leaving |
| `RUN` / `RUNSTOP` | `RUN\|heading`: a lease that must be renewed within 2s |
| `TURN` | `TURN\|heading` |
| `KEYRAW` | `KEYRAW\|key\|down/up/press[\|holdMs]` |
| `KEY` / `KEYSEQ` | `KEY\|name\|holdMs`, `KEYSEQ\|k1,k2,…\|holdMs` |
| `CHATIN` | `CHATIN\|text` |
| `SMTARGET` / `TARGET` | `…\|index` (reticle only / engage-or-switch) |
| `DISENGAGE`, `RAISE`, `FOLLOWSTOP` | |
| `FOLLOW` | `FOLLOW\|index` |
| `NPCPOKE` | `NPCPOKE\|index\|name` |
| `SELL` | `SELL\|npcIndex\|itemId,…` |
| `NPCREPLAY` | `NPCREPLAY\|npcIndex\|menuId\|hide(0/1)\|opt:unk:auto,…` |
| `CURREQ`, `GETINV` | |
| `LOT` / `PASS` | `LOT\|slot`, `PASS\|slot`: treasure pool (outgoing 0x041 / 0x042) |
| `WIDESCAN` | Ask the server for a zone-wide mob sweep (outgoing 0x0F4) |
| `JOBCHANGE` | `JOBCHANGE\|mainJobId\|subJobId` |
| `HUD` | `HUD\|line1~line2` |
| `VERBOSE`, `NOTIFY` | `…\|text` |

The tables above are the reference for the wire format; the addon source (`state.lua`, `control.lua`, `targeting.lua`, `npc.lua`, `inventory.lua`, `comm.lua`) carries no comments.

## Security note

The link has no authentication. Anything on this machine that can bind a port in 59332–59341 can make your characters move, chat or trade. The default loopback binding keeps it local to the machine. Change `BindAddress` only on a network you trust.
