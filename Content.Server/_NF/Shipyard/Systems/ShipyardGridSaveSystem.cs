using Content.Server._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared.Shuttles.Save; // For SendShipSaveDataClientMessage
using Content.Server.Maps;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Access.Components;
using Content.Shared.Atmos.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Power.Components;
using Content.Shared.VendingMachines;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using System.Numerics;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Robust.Shared.ContentPack;
using Robust.Server.Player;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Physics.Components;
using Robust.Shared.Containers;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Content.Server._NF.Shipyard.Systems;

/// <summary>
/// System for saving ships using the MapLoaderSystem infrastructure.
/// Saves ships as complete YAML files similar to savegrid command,
/// after cleaning them of problematic components and moving to exports folder.
/// </summary>
public sealed class ShipyardGridSaveSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly IResourceManager _resourceManager = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;

    private ISawmill _sawmill = default!;
    private MapLoaderSystem _mapLoader = default!;
    private SharedMapSystem _mapSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Initialize sawmill for logging
        _sawmill = Logger.GetSawmill("shipyard.gridsave");

        // Get the MapLoaderSystem reference
        _mapLoader = _entitySystemManager.GetEntitySystem<MapLoaderSystem>();
        _mapSystem = _entitySystemManager.GetEntitySystem<SharedMapSystem>();

        // Subscribe to shipyard console events
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleSaveMessage>(OnSaveShipMessage);
    }

    private void OnSaveShipMessage(EntityUid consoleUid, ShipyardConsoleComponent component, ShipyardConsoleSaveMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            _sawmill.Warning("No ID card in shipyard console slot");
            return;
        }

        if (!_entityManager.TryGetComponent<ShuttleDeedComponent>(targetId, out var deed))
        {
            _sawmill.Warning("ID card does not have a shuttle deed");
            return;
        }

        if (deed.ShuttleUid == null || !_entityManager.TryGetEntity(deed.ShuttleUid.Value, out var shuttleUid))
        {
            _sawmill.Warning("Shuttle deed does not reference a valid shuttle");
            return;
        }

        if (!_entityManager.TryGetComponent<MapGridComponent>(shuttleUid.Value, out var gridComponent))
        {
            _sawmill.Warning("Shuttle entity is not a valid grid");
            return;
        }

        // Get player session
        if (!_playerManager.TryGetSessionByEntity(player, out var playerSession))
        {
            _sawmill.Warning("Could not get player session");
            return;
        }

        _sawmill.Info($"Starting ship save for {deed.ShuttleName ?? "Unknown_Ship"} owned by {playerSession.Name}");

        // Save the ship using our new grid-based system
        _ = Task.Run(async () =>
        {
            var success = await TrySaveGridAsShip(shuttleUid.Value, deed.ShuttleName ?? "Unknown_Ship", playerSession.UserId.ToString(), playerSession);

            if (success)
            {
                // Clean up the deed after successful save
                _entityManager.RemoveComponent<ShuttleDeedComponent>(targetId);

                // Also remove any other shuttle deeds that reference this shuttle
                RemoveAllShuttleDeeds(shuttleUid.Value);

                _sawmill.Info($"Successfully saved and removed ship {deed.ShuttleName}");
            }
            else
            {
                _sawmill.Error($"Failed to save ship {deed.ShuttleName}");
            }
        });
    }

    /// <summary>
    /// Removes all ShuttleDeedComponents that reference the specified shuttle EntityUid
    /// </summary>
    private void RemoveAllShuttleDeeds(EntityUid shuttleUid)
    {
        var query = _entityManager.EntityQueryEnumerator<ShuttleDeedComponent>();
        var deedsToRemove = new List<EntityUid>();

        while (query.MoveNext(out var entityUid, out var deed))
        {
            if (deed.ShuttleUid != null && _entityManager.TryGetEntity(deed.ShuttleUid.Value, out var deedShuttleEntity) && deedShuttleEntity == shuttleUid)
            {
                deedsToRemove.Add(entityUid);
            }
        }

        foreach (var deedEntity in deedsToRemove)
        {
            _entityManager.RemoveComponent<ShuttleDeedComponent>(deedEntity);
            _sawmill.Info($"Removed shuttle deed from entity {deedEntity}");
        }
    }

    /// <summary>
    /// Saves a grid to a YAML file using MapLoaderSystem, after cleaning it of problematic components.
    /// </summary>
    public async Task<bool> TrySaveGridAsShip(EntityUid gridUid, string shipName, string playerUserId, ICommonSession playerSession)
    {
        if (!_entityManager.HasComponent<MapGridComponent>(gridUid))
        {
            _sawmill.Error($"Entity {gridUid} is not a valid grid");
            return false;
        }

        MapId? tempMapId = null;
        try
        {
            _sawmill.Info($"Starting ship save process for grid {gridUid} as '{shipName}'");

            // Step 1: Create a temporary blank map for saving
            tempMapId = _mapManager.CreateMap();
            _sawmill.Info($"Created temporary map {tempMapId}");

            // Step 2: Move the grid to the temporary map and clean it
            var tempGridUid = await MoveAndCleanGrid(gridUid, tempMapId.Value);
            if (tempGridUid == null)
            {
                _sawmill.Error("Failed to move and clean grid");
                return false;
            }

            _sawmill.Info($"Successfully moved and cleaned grid to {tempGridUid}");

            // Step 3: Save the grid using MapLoaderSystem to a temporary file
            var fileName = $"{shipName}.yml";
            var tempFilePath = new ResPath("/") / "UserData" / fileName;
            _sawmill.Info($"Attempting to save grid as {fileName}");

            bool success = _mapLoader.TrySaveGrid(tempGridUid.Value, tempFilePath);

            if (success)
            {
                _sawmill.Info($"Successfully saved grid to {fileName}");

                // Step 4: Read the YAML file and send to client
                try
                {
                    using var fileStream = _resourceManager.UserData.OpenRead(tempFilePath);
                    using var reader = new StreamReader(fileStream);
                    var yamlContent = await reader.ReadToEndAsync();

                    // Send the YAML data to the client for local saving
                    var saveMessage = new SendShipSaveDataClientMessage(shipName, yamlContent);
                    RaiseNetworkEvent(saveMessage, playerSession);

                    _sawmill.Info($"Sent ship data '{shipName}' to client {playerSession.Name} for local saving");

                    // Clean up the temporary server file with retry logic
                    await TryDeleteFileWithRetry(tempFilePath);
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to read/send YAML file: {ex}");
                    success = false;
                }
            }
            else
            {
                _sawmill.Error($"Failed to save grid to {fileName}");
            }

            return success;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception during ship save: {ex}");
            return false;
        }
        finally
        {
            // Step 6: Clean up temporary resources with proper timing
            if (tempMapId.HasValue)
            {
                // Give all systems significant time to finish processing the map deletion
                await Task.Delay(500);

                try
                {
                    _mapManager.DeleteMap(tempMapId.Value);
                    _sawmill.Info($"Cleaned up temporary map {tempMapId}");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to clean up temporary map {tempMapId}: {ex}");
                }
            }

            // Delete the original grid after all processing is complete
            if (_entityManager.EntityExists(gridUid))
            {
                // Additional delay to ensure all systems finish processing entity changes
                await Task.Delay(300);

                try
                {
                    _entityManager.DeleteEntity(gridUid);
                    _sawmill.Info($"Deleted original grid entity {gridUid}");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to delete original grid entity {gridUid}: {ex}");
                }
            }
        }
    }

    /// <summary>
    /// STEP 2: Empty the contents of any container on the grid, then clean the grid of problematic components
    /// and delete freefloating entities that are not anchored or connected to the grid
    /// </summary>
    private async Task<bool> Step2_EmptyContainersAndCleanGrid(EntityUid gridUid)
    {
        try
        {
            _sawmill.Info("Step 2: Emptying containers and cleaning grid");

            var allEntities = new HashSet<EntityUid>();

            // Get all entities on the grid
            if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            {
                var gridBounds = grid.LocalAABB;
                var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
                foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
                {
                    if (entity != gridUid) // Don't include the grid itself
                        allEntities.Add(entity);
                }
            }

            _sawmill.Info($"Found {allEntities.Count} entities to process");

            var entitiesRemoved = 0;
            var containersEmptied = 0;

            // First pass: Empty all containers
            foreach (var entity in allEntities.ToList())
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Empty any containers by deleting their contents
                if (_entityManager.TryGetComponent<ContainerManagerComponent>(entity, out var containerManager))
                {
                    foreach (var container in containerManager.Containers.Values)
                    {
                        var containedEntities = container.ContainedEntities.ToList();
                        foreach (var containedEntity in containedEntities)
                        {
                            try
                            {
                                _entityManager.DeleteEntity(containedEntity);
                                entitiesRemoved++;
                            }
                            catch (Exception ex)
                            {
                                _sawmill.Warning($"Failed to delete contained entity {containedEntity}: {ex}");
                            }
                        }
                        if (containedEntities.Count > 0)
                        {
                            containersEmptied++;
                            _sawmill.Info($"Emptied container with {containedEntities.Count} items");
                        }
                    }
                }
            }

            // Second pass: Delete freefloating entities (not anchored or connected to grid)
            var freefloatingEntities = new List<EntityUid>();
            foreach (var entity in allEntities)
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Skip structural grid components
                if (_entityManager.HasComponent<MapGridComponent>(entity))
                    continue;

                // Check if entity is anchored or in a container
                if (_entityManager.TryGetComponent<TransformComponent>(entity, out var transform))
                {
                    // If not anchored and not in a container, mark for deletion
                    if (!transform.Anchored && !_containerSystem.IsEntityInContainer(entity))
                    {
                        freefloatingEntities.Add(entity);
                    }
                }
            }

            // Delete freefloating entities
            foreach (var entity in freefloatingEntities)
            {
                try
                {
                    if (_entityManager.EntityExists(entity))
                    {
                        _entityManager.DeleteEntity(entity);
                        entitiesRemoved++;
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Warning($"Failed to delete freefloating entity {entity}: {ex}");
                }
            }

            _sawmill.Info($"Step 2 complete: Emptied {containersEmptied} containers, removed {entitiesRemoved} entities");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 2 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 3: Delete any vending machines or remaining structures that would pose a problem to ship saving
    /// </summary>
    private async Task<bool> Step3_DeleteProblematicStructures(EntityUid gridUid)
    {
        try
        {
            _sawmill.Info("Step 3: Deleting problematic structures");

            var allEntities = new HashSet<EntityUid>();

            // Get all remaining entities on the grid
            if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            {
                var gridBounds = grid.LocalAABB;
                var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
                foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
                {
                    if (entity != gridUid) // Don't include the grid itself
                        allEntities.Add(entity);
                }
            }

            var structuresRemoved = 0;
            var componentsRemoved = 0;

            foreach (var entity in allEntities.ToList())
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Delete vending machines completely
                if (_entityManager.HasComponent<VendingMachineComponent>(entity))
                {
                    _sawmill.Info($"Removing vending machine entity {entity}");
                    _entityManager.DeleteEntity(entity);
                    structuresRemoved++;
                    continue;
                }

                // Remove problematic components from remaining entities

                // Note: Removed PhysicsComponent deletion that was causing collision issues in loaded ships
                // PhysicsComponent and FixturesComponent are needed for proper collision detection

                // Remove atmospheric components that hold runtime state

                // Reset power components to clean state
            }

            _sawmill.Info($"Step 3 complete: Removed {structuresRemoved} problematic structures, cleaned {componentsRemoved} components");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 3 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Moves a grid to a temporary map and cleans it for saving.
    /// </summary>
    private async Task<EntityUid?> MoveAndCleanGrid(EntityUid originalGridUid, MapId targetMapId)
    {
        try
        {
            // Move the grid to the temporary map and normalize its rotation
            var gridTransform = _entityManager.GetComponent<TransformComponent>(originalGridUid);
            _transformSystem.SetCoordinates(originalGridUid, new EntityCoordinates(_mapManager.GetMapEntityId(targetMapId), System.Numerics.Vector2.Zero));

            // Normalize grid rotation to 0 degrees
            _transformSystem.SetLocalRotation(originalGridUid, Angle.Zero);

            _sawmill.Info($"Moved grid {originalGridUid} to temporary map {targetMapId} and normalized rotation");

            // Clean the grid of problematic components
            CleanGridForSaving(originalGridUid);

            return originalGridUid;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to move and clean grid: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Removes problematic components from a grid before saving.
    /// This includes session-specific data, vending machines, runtime state, etc.
    /// Also removes ALL unanchored/unattached entities and empties all containers.
    /// </summary>
    public void CleanGridForSaving(EntityUid gridUid)
    {
        _sawmill.Info($"Starting comprehensive grid cleanup for {gridUid}");

        var allEntities = new HashSet<EntityUid>();

        // Get all entities on the grid
        if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
        {
            var gridBounds = grid.LocalAABB;
            var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
            foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
            {
                if (entity != gridUid) // Don't include the grid itself
                    allEntities.Add(entity);
            }
        }

        _sawmill.Info($"Found {allEntities.Count} entities to clean on grid");

        var entitiesRemoved = 0;
        var componentsRemoved = 0;
        var containersEmptied = 0;

        // First pass: Empty all containers to prevent saving contained entities
        foreach (var entity in allEntities.ToList())
        {
            try
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Empty all containers
                if (_entityManager.TryGetComponent<ContainerManagerComponent>(entity, out var containerManager))
                {
                    foreach (var container in containerManager.Containers.Values)
                    {
                        var containedEntities = container.ContainedEntities.ToList();
                        foreach (var containedEntity in containedEntities)
                        {
                            _sawmill.Info($"Removing contained entity {containedEntity} from container in {entity}");
                            _entityManager.DeleteEntity(containedEntity);
                            entitiesRemoved++;
                        }
                        containersEmptied++;
                    }
                }
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Error emptying containers for entity {entity}: {ex}");
            }
        }

        // Second pass: Remove all unanchored/unattached entities
        foreach (var entity in allEntities.ToList())
        {
            try
            {
                // Check if entity still exists before processing
                if (!_entityManager.EntityExists(entity))
                {
                    _sawmill.Warning($"Entity {entity} no longer exists during cleanup, skipping");
                    continue;
                }

                // Skip the grid itself and other fundamental map components
                if (_entityManager.HasComponent<MapGridComponent>(entity) ||
                    _entityManager.HasComponent<MapComponent>(entity))
                {
                    continue;
                }

                // Get transform component to check anchoring
                if (_entityManager.TryGetComponent<TransformComponent>(entity, out var transform))
                {
                    // Delete ALL entities that are not anchored to the grid
                    if (!transform.Anchored)
                    {
                        _sawmill.Info($"Removing unanchored entity {entity} during comprehensive cleanup");
                        _entityManager.DeleteEntity(entity);
                        entitiesRemoved++;
                        continue; // Skip to next entity since this one was deleted
                    }
                }
                else
                {
                    // If no transform component, it's probably not needed for ship structure
                    _sawmill.Info($"Removing entity {entity} with no transform component");
                    _entityManager.DeleteEntity(entity);
                    entitiesRemoved++;
                    continue;
                }

                // For anchored entities, clean their components but keep the entity
                // Remove session-specific components that shouldn't be saved
                if (_entityManager.RemoveComponent<ActorComponent>(entity))
                    componentsRemoved++;
                if (_entityManager.RemoveComponent<EyeComponent>(entity))
                    componentsRemoved++;

                // Note: Removed PhysicsComponent deletion that was causing collision issues in loaded ships
                // PhysicsComponent and FixturesComponent are needed for proper collision detection

                // Reset power components to clean state through the proper system
                if (_entityManager.TryGetComponent<BatteryComponent>(entity, out var battery))
                {
                    // Use the battery system instead of direct access
                    if (_entitySystemManager.TryGetEntitySystem<BatterySystem>(out var batterySystem))
                    {
                        batterySystem.SetCharge(entity, battery.MaxCharge);
                    }
                }

                // Remove problematic atmospheric state
                if (_entityManager.RemoveComponent<AtmosDeviceComponent>(entity))
                    componentsRemoved++;

                // Remove any other problematic components
                // Note: We're being conservative here - removing things that commonly cause issues
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Error cleaning entity {entity}: {ex}");
            }
        }

        _sawmill.Info($"Comprehensive grid cleanup complete: removed {entitiesRemoved} entities, emptied {containersEmptied} containers, removed {componentsRemoved} components from remaining entities");
    }

    /// <summary>
    /// Writes YAML data to a temporary file in UserData for loading
    /// </summary>
    public async Task<bool> WriteYamlToUserData(string fileName, string yamlData)
    {
        try
        {
            var userDataPath = _resourceManager.UserData;
            var resPath = new ResPath(fileName);

            await using var stream = userDataPath.OpenWrite(resPath);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(yamlData);

            _sawmill.Info($"Temporary YAML file written: {resPath}");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to write temporary YAML file {fileName}: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to delete a file with retry logic to handle file access conflicts
    /// </summary>
    private async Task TryDeleteFileWithRetry(ResPath filePath, int maxRetries = 3, int delayMs = 100)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                _resourceManager.UserData.Delete(filePath);
                _sawmill.Info($"Successfully deleted temporary server file {filePath}");
                return;
            }
            catch (IOException ex) when (attempt < maxRetries - 1)
            {
                _sawmill.Warning($"File deletion attempt {attempt + 1} failed for {filePath}: {ex.Message}. Retrying in {delayMs}ms...");
                await Task.Delay(delayMs);
                delayMs *= 2; // Exponential backoff
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to delete temporary file {filePath} on attempt {attempt + 1}: {ex}");
                if (attempt == maxRetries - 1)
                {
                    _sawmill.Error($"Giving up on deleting {filePath} after {maxRetries} attempts");
                }
                break;
            }
        }
    }

}
