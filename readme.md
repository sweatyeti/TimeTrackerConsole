# todo

 - [ ] allow clearing of the task
 - [ ] in the list of entries highlight the in-progress one
 - [x] when starting a new entry ask for the task up front
 - [x] if updating the in-progress entry don't prompt for logging
 - [x] don't ask to log entries that don't have tasks
 - [x] make the list of tasks be part of the menu itself? Then can use arrow keys or hotkeys to select certain pieces or functionalities?

with the new menu style I added a `--old-menu` switch to the `new` verb when starting the app
right now this just flips an internal flag that runs different code paths, but eventually they could be merged together more cleanly