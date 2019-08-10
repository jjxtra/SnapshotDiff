# SnapshotDiff
Get email notifications when a snapshot of a url changes (image compare).

## Install
First install chrome, latest version. Then run as normal. A self contained build will ensure .NET core is not required.
You may need to place the chromedriver in the same folder as your install path as the .NET core publish command does not seem to pick this up.

## Running
Run the application and pass the json file as the only argument. The json file has a 'process' and 'commands' section. The 'process' section controls the executable to run, by default chrome on Ubuntu. The 'commands' section contains an array of commands to fetch urls preiodically.

## Arguments for json file commands

Url - the url to ping  
FileName - the file name to save the current image to, usually a .png file
Width - chrome browser width  
Height - chrome browser height  
TimeoutMilliseconds - how long to time out if page load takes too long
Percent - the percent the pixels must change to notify  
LoopDelaySeconds - how often to loop and try the url again  
EmailTestOnly - whether to just send a test email  
EmailHost - email server address  
EmailPort - email server port  
EmailUserName - email server user name to login with  
EmailPassword - email password to login with  
EmailFromAddress - email from address to send emails from  
EmailFromName - email from name  
EmailToAddress - email to address  
EmailSubject - email subject, {0} will be replaced with the url  

