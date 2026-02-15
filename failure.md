# What does alive mean?
Alive means that a node has been pinged for a heart beat and has responded in the last 20 seconds.

# What does suspect mean?
Suspect means that a node has been pinged for a heart beat and has responsed in the last minute.

# What does dead mean?
Dead means the node hasn't responded to a heartbeat, or hasn't responded to a heart beat ping for longer than a minute.

# Can dead -> alive occur?
Yes a node will continuously be pinged for a heartbeat even if it's considered dead, if it was to respond to one of 
these pings, it would be considered alive again.
For example if a node goes offline, after a minute the other nodes in the cluster will consider it dead but will continue to ping
it, once the node comes back it will respond to a ping message and the other nodes will consider it alive again.

# What does the system do when a node is dead?
Nothing at the moment, the system doesn't contain enough functionality to do anything with that information.