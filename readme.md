# todo

 - [x] allow clearing of the task (using a `none` string to do this)
 - [x] in the list of entries highlight the in-progress one (used a bold green style for "In progress")
 - [x] when starting a new entry ask for the task up front
 - [x] if updating the in-progress entry don't prompt for logging
 - [x] don't ask to log entries that don't have tasks
 - [x] make the list of tasks be part of the menu itself? Then can use arrow keys or hotkeys to select certain pieces or functionalities?

with the new menu style I added a `--old-menu` switch to the `new` verb when starting the app
right now this just flips an internal flag that runs different code paths, but eventually they could be merged together more cleanly'

right now there's a branch for adding a backup/save functionality, and that's the reason the in-memory store is a ConcurrentDictionary.. is the overhead of ConcurrentDictionary throughout the rest of the app necessary? When doing a backup can perhaps just do an in-progress spinner or something then cancel it if it's taking too long so it's all single-threaded and thus can go back to using a List or regular Dictionary