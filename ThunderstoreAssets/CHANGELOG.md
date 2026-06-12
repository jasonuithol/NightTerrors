# Changelog

## 1.0.3

- Updated BepInEx dependency to 5.4.2333

## 1.0.2

- Removed SwapEquipment scenario — inventory swapping between players has been removed to improve reliability
- Fixed inventory restore failing when players were dead/respawning at the time the restore RPC arrived — restores are now deferred and applied on spawn
- Reduced pre-event delay from 2.2s to 1.5s (swap upload wait no longer needed)
- Removed UploadInventory and ReceiveInventory RPCs (no longer needed without swap)
- Updated config default for ScenarioWeights from "1,1,1,1" to "1,1,1"

## 1.0.1

- Initial Thunderstore release
