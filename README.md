# SnapshotDiff
Get email notifications when a snapshot of a url changes (image compare).

## Install
First install chrome, latest version. Then run as normal. A self contained build will ensure .NET core is not required.

## Arguments

Url - the url to ping
FileName - the file name to save the current image to
Width - chrome browser width
Height - chrome browser height
LoadDelayMilliseconds - time to wait for page load complete event
ForceDelayMilliseconds - force sleep this amount of time after page load event
Percent - the percent the pixels must change to notify
EmailTestOnly - whether to just send a test email
EmailHost - email server address
EmailPort - email server port
EmailUserName - email server user name to login with
EmailPassword - email password to login with
EmailFromAddress - email from address to send emails from
EmailFromName - email from name
EmailToAddress - email to address
EmailSubject - email subject, {0} will be replaced with the url
