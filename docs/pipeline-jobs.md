# Pipeline Jobs (RabbitMQ + shared folder)

The AR instance receives new scenes from the TRIFFID pipeline the same way the rest of the pipeline works: the files travel through a shared folder and RabbitMQ only carries the notification. The ground station Unity instance converts the splat, stages its own scene, then writes the job to the samba share and publishes `ar.ready` (`ArJobPublisher` in UnityGaussianSplattingAutoWatcher). Here, the editor script `Assets/Editor/ArSplatInboxWatcher.cs` picks the job up; it runs while the project is open in the Editor.

## Setup

The full step-by-step for both machines (GS on Linux, this project on a Windows laptop), with a test
that needs no new flight and a troubleshooting table, is in the pipeline repository:
`triffid-full-pipeline/docs/ar-laptop-connection.md`. In short:

1. Copy `.env.example` to `.env` at the repo root and set `RABBITMQ_HOST` to the pipeline machine's LAN address plus the broker credentials. On the pipeline machine, set `RABBITMQ_BIND` in its `.env` to that LAN address so the laptop can reach the broker. RabbitMQ's `guest` user only logs in from localhost, so use a real user.
2. Connect to the job share. The pipeline machine shares its AR hand-off folder read-only over SMB: its `samba` service (compose profile `samba`) with `SAMBA_SHARE` (default `triffid`), `SAMBA_USER` and `SAMBA_PASS` from its `.env`, on the address in `SAMBA_BIND`. Log in once, then set `TRIFFID_SAMBA_DIR` to the share:
   - **Windows:** `net use \\192.168.1.50\triffid /user:triffid /persistent:yes` (asks for the password), then `TRIFFID_SAMBA_DIR=\\192.168.1.50\triffid`. A mapped drive works too (`net use Z: \\192.168.1.50\triffid /user:triffid /persistent:yes`, then `TRIFFID_SAMBA_DIR=Z:\`). Run `net use` as the same user that starts Unity Hub, not from an administrator prompt, or the Editor will not see it.
   - **Linux:** `sudo mount -t cifs //192.168.1.50/triffid /mnt/triffid -o ro,username=triffid,uid=$(id -u),gid=$(id -g)` (asks for the password; needs `cifs-utils`), then `TRIFFID_SAMBA_DIR=/mnt/triffid`.

   Without `TRIFFID_SAMBA_DIR`, jobs are looked for in `samba_folder/` at the repo root (local testing). If the share is not reachable when a job arrives, the job is put back in the queue, not lost.
3. Open the project. The Console shows a `Watching queue …` line with the broker, the credential source and the samba folder in use.

## Job message

Routing key `ar.ready` on the `pipeline.events` topic exchange (queue `ar.queue`). Each job is a `<stem>/` folder in the samba share:

```json
{
  "job_id": "flight_001",
  "splat_asset_path": "flight_001/flight_001.asset",
  "splat_data_paths": ["flight_001/flight_001_pos.bytes", "flight_001/flight_001_oth.bytes",
                       "flight_001/flight_001_col.bytes", "flight_001/flight_001_shs.bytes"],
  "obj_path": "flight_001/flight_001.obj",
  "transform_json_path": "flight_001/flight_001.transform_colmap_to_enu.json"
}
```

The splat is the Gaussian splat asset already converted by the ground station. It references its data files by GUID, so the `.asset` and every `.bytes` file must have their `.meta` files next to them. `splat_asset_path` and `splat_data_paths` are required; the mesh and geolocal json are optional. Paths are relative to the samba folder.

For each job the watcher:

1. Imports the asset and data files (with their `.meta` files), the obj and the json into `Assets/TriffidJobs/<stem>/`, replacing an earlier import of the same job. The shared copies are left in place. Every one of them is required: a job without its mesh or transform, or with a file missing from the share, is dropped with the reason in the Console.
2. Writes the asset, mesh and transform paths to `Assets/StreamingAssets/project_paths.local.json` (git-ignored).
3. Stages the scene, the same way the ground station stages its own: the tracked template `Assets/Scenes/TRIFFID.unity` is copied to the git-ignored `Assets/Staged/TRIFFID_staged.unity`, and in the copy the Gaussian renderer, rotation aligner, JSON spawner and mesh loader are switched to `project_paths.local.json`, the asset and mesh are loaded, then the JSON rotation is applied and the collider synced onto the new mesh. The copy is saved. The template and the tracked `project_paths.json` it reads are never changed, so a job never shows up as a change in git. Detections are not part of the job yet; `ArSceneStager.StageDetections` is the empty slot for them.

Which scene you see afterwards:
- **The staged scene is open:** the job is staged into it in place and saved.
- **Only the template, without unsaved changes, or the empty startup scene is open:** the Editor switches to the freshly staged scene.
- **Any other scene, or unsaved changes:** nothing you have open is replaced. The job is staged in the background and the Console says so; open `Assets/Staged/TRIFFID_staged.unity` to see it.

The staged scene belongs to the pipeline: it is saved after every job, even if it shows unsaved changes (its components mark it modified just by loading), so anything changed by hand in it is overwritten by the next job. A staged scene that is not open is refreshed from the template for every job, so nothing of an earlier job carries over. Lasting changes belong in the template, `Assets/Scenes/TRIFFID.unity`.

Jobs wait in the queue during Play Mode and are staged when you exit it. An unreachable samba folder requeues the job; a job with missing files, or one that cannot be staged, is dropped with the reason in the Console.

To test without the ground station, put a job folder in the samba folder and run `LocalScripts/publish_ar_job.py <stem>` where the broker's management API is reachable (on the pipeline machine).
