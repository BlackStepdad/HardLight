using Content.Shared.Shuttles.Components;
using Content.Shared.Procedural;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Dataset;
using Robust.Shared.Prototypes;
using Content.Shared.Popups; // Frontier
using Content.Shared._NF.CCVar; // Frontier
using Content.Server.Station.Components; // Frontier
using Robust.Shared.Map.Components; // Frontier
using Robust.Shared.Physics.Components; // Frontier
using Content.Shared.NPC; // Frontier
using Content.Server._NF.Salvage; // Frontier
using Content.Shared.NPC.Components; // Frontier
using Content.Server.Salvage.Expeditions; // Frontier
using Content.Shared.Mind.Components; // Frontier
using Content.Shared.Mobs.Components; // Frontier
using Robust.Shared.Physics; // Frontier

namespace Content.Server.Salvage;

public sealed partial class SalvageSystem
{
    [ValidatePrototypeId<EntityPrototype>]
    public const string CoordinatesDisk = "CoordinatesDisk";

    [Dependency] private readonly SharedPopupSystem _popupSystem = default!; // Frontier
    [Dependency] private readonly SalvageSystem _salvage = default!; // Frontier

    private const float ShuttleFTLMassThreshold = 50f; // Frontier
    private const float ShuttleFTLRange = 150f; // Frontier

    private void OnSalvageClaimMessage(EntityUid uid, SalvageExpeditionConsoleComponent component, ClaimSalvageMessage args)
    {

        // Use the grid/entity the console is on, not the station
        var gridEntity = uid;
        if (!TryComp<SalvageExpeditionDataComponent>(gridEntity, out var data) || data.Claimed)
            return;

        if (!data.Missions.TryGetValue(args.Index, out var missionparams))
            return;

        // Frontier: prevent expeditions if there are too many out already.
        var activeExpeditionCount = 0;
        var expeditionQuery = AllEntityQuery<SalvageExpeditionDataComponent, MetaDataComponent>();
        while (expeditionQuery.MoveNext(out var expeditionUid, out _, out _))
        {
            if (TryComp<SalvageExpeditionDataComponent>(expeditionUid, out var expeditionData) && expeditionData.Claimed)
                activeExpeditionCount++;
        }

        if (activeExpeditionCount >= _cfgManager.GetCVar(NFCCVars.SalvageExpeditionMaxActive))
        {
            PlayDenySound((uid, component));
            _popupSystem.PopupEntity(Loc.GetString("shuttle-ftl-too-many"), uid, PopupType.MediumCaution);
            UpdateConsoles((gridEntity, data));
            return;
        }
        // End Frontier

        // var cdUid = Spawn(CoordinatesDisk, Transform(uid).Coordinates); // Frontier: no disk-based FTL
        // SpawnMission(missionparams, station.Value, cdUid); // Frontier: no disk-based FTL

        // Frontier: FTL travel is currently restricted to expeditions and such, and so we need to put this here
        #region Frontier FTL changes
        // until FTL changes for us in some way.

        // Run a proximity check (unless using a debug console)
        if (_salvage.ProximityCheck && !component.Debug)
        {
            if (!TryComp<MapGridComponent>(gridEntity, out var gridComp))
            {
                PlayDenySound((uid, component));
                _popupSystem.PopupEntity(Loc.GetString("shuttle-ftl-invalid"), uid, PopupType.MediumCaution);
                UpdateConsoles((gridEntity, data));
                return;
            }

            if (HasComp<FTLComponent>(gridEntity))
            {
                PlayDenySound((uid, component));
                _popupSystem.PopupEntity(Loc.GetString("shuttle-ftl-recharge"), uid, PopupType.MediumCaution);
                UpdateConsoles((gridEntity, data));
                return;
            }

            var xform = Transform(gridEntity);
            var bounds = _transform.GetWorldMatrix(gridEntity).TransformBox(gridComp.LocalAABB).Enlarged(ShuttleFTLRange);
            var bodyQuery = GetEntityQuery<PhysicsComponent>();
            var otherGrids = new List<Entity<MapGridComponent>>();
            _mapManager.FindGridsIntersecting(xform.MapID, bounds, ref otherGrids);
            foreach (var otherGrid in otherGrids)
            {
                if (gridEntity == otherGrid.Owner ||
                    !bodyQuery.TryGetComponent(otherGrid.Owner, out var body) ||
                    body.Mass < ShuttleFTLMassThreshold && body.BodyType == BodyType.Dynamic)
                {
                    continue;
                }

                PlayDenySound((uid, component));
                _popupSystem.PopupEntity(Loc.GetString("shuttle-ftl-proximity"), uid, PopupType.MediumCaution);
                UpdateConsoles((gridEntity, data));
                return;
            }
        }
        SpawnMission(missionparams, gridEntity, null);
        #endregion Frontier FTL changes
        // End Frontier

    data.ActiveMission = args.Index;
    var mission = GetMission(missionparams.MissionType, _prototypeManager.Index<SalvageDifficultyPrototype>(missionparams.Difficulty), missionparams.Seed); // Frontier: add MissionType
    // Frontier - TODO: move this to progression for secondary window timer
    data.NextOffer = _timing.CurTime + mission.Duration + TimeSpan.FromSeconds(1);
    data.CooldownTime = mission.Duration + TimeSpan.FromSeconds(1); // Frontier

    // _labelSystem.Label(cdUid, GetFTLName(_prototypeManager.Index<LocalizedDatasetPrototype>("NamesBorer"), missionparams.Seed)); // Frontier: no disc
    // _audio.PlayPvs(component.PrintSound, uid); // Frontier: no disc

    UpdateConsoles((gridEntity, data));
    }

    // Frontier: early expedition end
    private void OnSalvageFinishMessage(EntityUid entity, SalvageExpeditionConsoleComponent component, FinishSalvageMessage e)
    {
        // Use the entity/grid directly
        var gridEntity = entity;
        if (!TryComp<SalvageExpeditionDataComponent>(gridEntity, out var data) || !data.CanFinish)
            return;

        // Based on SalvageSystem.Runner:OnConsoleFTLAttempt
        if (!TryComp(entity, out TransformComponent? xform)) // Get the console's grid (if you move it, rip you)
        {
            PlayDenySound((entity, component));
            _popupSystem.PopupEntity(Loc.GetString("salvage-expedition-shuttle-not-found"), entity, PopupType.MediumCaution);
            UpdateConsoles((gridEntity, data));
            return;
        }

        // Frontier: check if any player characters or friendly ghost roles are outside
        var query = EntityQueryEnumerator<MindContainerComponent, MobStateComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var mindContainer, out var _, out var mobXform))
        {
            if (mobXform.MapUid != xform.MapUid)
                continue;

            // Not player controlled (ghosted)
            if (!mindContainer.HasMind)
                continue;

            // NPC, definitely not a person
            if (HasComp<ActiveNPCComponent>(uid) || HasComp<NFSalvageMobRestrictionsComponent>(uid))
                continue;

            // Hostile ghost role, continue
            if (TryComp(uid, out NpcFactionMemberComponent? npcFaction))
            {
                var hostileFactions = npcFaction.HostileFactions;
                if (hostileFactions.Contains("NanoTrasen")) // TODO: move away from hardcoded faction
                    continue;
            }

            // Okay they're on salvage, so are they on the shuttle.
            if (mobXform.GridUid != xform.GridUid)
            {
                PlayDenySound((entity, component));
                _popupSystem.PopupEntity(Loc.GetString("salvage-expedition-not-everyone-aboard", ("target", uid)), entity, PopupType.MediumCaution);
                UpdateConsoles((gridEntity, data));
                return;
            }
        }
        // End SalvageSystem.Runner:OnConsoleFTLAttempt

    data.CanFinish = false;
    UpdateConsoles((gridEntity, data));

        var map = Transform(entity).MapUid;

        if (!TryComp<SalvageExpeditionComponent>(map, out var expedition))
            return;

        const int departTime = 20;
        var newEndTime = _timing.CurTime + TimeSpan.FromSeconds(departTime);

        if (expedition.EndTime <= newEndTime)
            return;

        expedition.Stage = ExpeditionStage.FinalCountdown;
        expedition.EndTime = newEndTime;
        Dirty(map.Value, expedition);

        Announce(map.Value, Loc.GetString("salvage-expedition-announcement-early-finish", ("departTime", departTime)));
    }
    // End Frontier: early expedition end

    private void OnSalvageConsoleInit(Entity<SalvageExpeditionConsoleComponent> console, ref ComponentInit args)
    {
    // Always ensure SalvageExpeditionDataComponent is present and missions are generated
    var gridEntity = console.Owner;
    var data = EnsureComp<SalvageExpeditionDataComponent>(gridEntity);
    data.ActiveMission = 0;
    data.Cooldown = false;
    data.CanFinish = false;
    data.NextOffer = _timing.CurTime;
    data.CooldownTime = TimeSpan.Zero;
    data.Missions.Clear();
    _salvage.GenerateMissions(data);
    UpdateConsole(console);
    }

    private void OnSalvageConsoleParent(Entity<SalvageExpeditionConsoleComponent> console, ref EntParentChangedMessage args)
    {
        UpdateConsole(console);
    }

    private void UpdateConsoles(Entity<SalvageExpeditionDataComponent> component)
    {
        var state = GetState(component);

        var query = AllEntityQuery<SalvageExpeditionConsoleComponent, UserInterfaceComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var uiComp, out var xform))
        {
            // Use the grid/entity directly
            if (uid != component.Owner)
                continue;

            _ui.SetUiState((uid, uiComp), SalvageConsoleUiKey.Expedition, state);
        }
    }

    private void UpdateConsole(Entity<SalvageExpeditionConsoleComponent> component)
    {
        var gridEntity = component.Owner;
        SalvageExpeditionConsoleState state;

        if (TryComp<SalvageExpeditionDataComponent>(gridEntity, out var dataComponent))
        {
            state = GetState(dataComponent);
        }
        else
        {
            state = new SalvageExpeditionConsoleState(TimeSpan.Zero, false, true, 0, new List<SalvageMissionParams>(), false, TimeSpan.FromSeconds(1));
        }

        // If we have a lingering FTL component, we cannot start a new mission
        if (HasComp<FTLComponent>(gridEntity))
        {
            state.Cooldown = true; //Hack: disable buttons
        }

        _ui.SetUiState(component.Owner, SalvageConsoleUiKey.Expedition, state);
    }

    // Frontier: deny sound
    private void PlayDenySound(Entity<SalvageExpeditionConsoleComponent> ent)
    {
        _audio.PlayPvs(_audio.ResolveSound(ent.Comp.ErrorSound), ent);
    }
    // End Frontier
}
