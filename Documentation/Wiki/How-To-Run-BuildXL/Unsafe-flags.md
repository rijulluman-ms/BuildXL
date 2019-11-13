# Unsafe Options
## Reason why we have unsafe
BuildXL guarantees reliable builds by default. You don't have to clean your build ever and all builds are distributable.
This requires BuildXL to know what the tool is doing and do things deterministically.

Sometimes this level of guarantee make it prohibitively difficult to specify the build graph or achieve a high level of cacheability. So it is sometimes necessary to trick or inform the build engine into doing less work than is strictly needed to produce the correct outputs.

BuildXL ensures that all these options are clearly marked as "Unsafe".
 * On the command line all these flags start with `/unsafe_`
 * In the SDKs when creating a pip these options are clearly marked on the Unsafe member.

When using these features BuildXL ensures that the results produced by tools run with these flags are cached differently than when run without, by salting the cache entries with the unsafe options.

## What can go wrong?
Ttechnically when using these unsafe flags you can end up with unexpected outputs. For example:
If you use the flag `/unsafe_preserveOutputs` you are asking BuildXL not to delete the output files before running a process. Let's say your tool has some internal timestamp based instrumentality like build.exe. Now if one of the output files happens to end up with a timestamp far into the future, no matter how you change the inputs the tool will not reproduce any outputs.

## How to recover
Since BuildXL adds the unsafe options into the cache fingerprint you should get back to a proper reliable build by doing a build without any of the unsafe flags.
