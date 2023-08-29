# Azure Pipelines Agent on s390x Red Hat Enterprise Linux release 8.7


## Agent build 

Install needed pakages:
```console
$ sudo dnf install git
$ sudo dnf install dotnet-sdk-6.0
$ sudo dnf install zip
```

in your home folder:

```console
$ mkdir gitRepos
$ cd gitRepos
$ git clone https://Sogei1Collection@dev.azure.com/Sogei1Collection/Innovazione-z/_git/azure-pipelines-agent-s390x
$ cd azure-pipelines-agent-s390x/src
$ ./dev.sh l Release linux-s390x
$ ./dev.sh b Release linux-s390x
```

In _layout folder the project build created the agent-linux-s390x folder.


## Agent setup

Azure documentation reference: https://learn.microsoft.com/en-us/azure/devops/pipelines/agents/v2-linux?view=azure-devops

In agent-linux-s390x folder:

```console
$ sudo ./bin/installdependencies.sh
$ ./config.sh (ignore warnings)
```

When promped insert as follow:

- url: https://dev.azure.com/(your organization name) for example https://dev.azure.com/Sogei-POC
- PAT: generate a PAT following the instructions reported on the link (for example h7f2elimn2yhn7otop2fz322qktjs7rknbxad5tq6n5itsnvpv7a)
- pool name: select an already created pool onyour azure devops organization, for example test-z

Leave other settings as default
	
open the .env file and add the following rows:

```console
VSTS_HTTP_PROXY=http://proxyappl.finanze.it:80 
https_proxy=http://proxyappl.finanze.it:80 
http_proxy=http://proxyappl.finanze.it:80 
no_proxy=.sogei.it,127.0.0.1,192.168.0.0/24,26.0.0.0/8,.sogeiaz.it,sogeiazlab.it
```

Start the agent as a service:

```console
$ sudo ./svc.sh install
$ sudo ./svc.sh start
```

To check the service is properly running:

```console
$ sudo ./svc.sh status
```


