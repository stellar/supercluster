# Creating a Supercluster Compatible EKS Cluster

This document explains how to use the `eks-init.py` script in the `scripts`
directory to build a supercluster compatible EKS cluster on AWS.

## Requirements

The script requires that you have the following programs installed:
* AWS CLI
* eksctl
* kubectl
* helm
* dig
* ssh

### Set up AWS CLI tools

Before running the script, please follow
[Amazon's guide for setting up tools to manage EKS clusters](https://docs.aws.amazon.com/eks/latest/userguide/setting-up.html).

### AWS Quotas

In order to run the theoretical max TPS test with the recommended parameters,
you'll need a quota of at least 160 vCPUs for on-demand M instance types in the
region you plan to run the test in.

## Running the Script

`eks-init.py` is an interactive script that automates the creation and
configuration of a supercluster compatible EKS cluster. The script periodically
prompts for information to configure the cluster. The majority of prompts have
default values in brackets. Pressing `<enter>` will choose the default value.
Otherwise, type your desired value followed by `<enter>`.

### Initial Prompts

To start, `eks-init.py` will ask for the following:

* Cluster name: The name you would like give the cluster.
* Region: The AWS region you would like to create the cluster in.
* SSH public key: The public key you would like to use for SSH access into the
  instances the script will spin up.

### Creating a Cluster

Next, the script will ask if you would like to create an EKS cluster.  You
should only say "no" here if you have already created an EKS cluster and you are
using the script to configure it.  If you say "yes", the script will ask for the
following:

* Number of nodes: The number of EC2 instances to create. We recommend 10 if you
  plan to run the theoretical max TPS test.
* Node type: The EC2 instance type to use for each node. We recommend
  `m5d.4xlarge` if you plan to run the theoretical max TPS test.

After answering the previous prompts, the script will create a cluster using
`eksctl`. This may take 10-20 minutes. Once it is complete the script will list
the public IP addresses of the nodes in the cluster.

### Mounting NVMe Drives

Next, the script will ask if you would like to mount NVMe drives on the cluster
instances. If you are using an instance type with NVMe drives and you have not
manually mounted them yourself, then you should answer "yes". Answering "yes"
will result in the following prompt:

* SSH private key: Private key corresponding to the public key you provided earlier.

After answering the previous prompt, the script will SSH into each instance to
format and mount the NVMe drives such that kubernetes pods will run on them.

### Installing an Ingress Controller

Next, the script will ask if you would like to install an ingress controller.
This is required for supercluster, so you should say "yes" unless you plan to
manually install an ingress controller. Upon answering "yes", the script will
install the controller. There are no additional prompts to this portion of the
script.

### Configuring DNS

Next, the script will prompt for your ingress domain. This is the domain name
supercluster will use to communicate with the stellar-core nodes running on the
cluster. This may be a subdomain (such as `ssc.example.com`).

After entering the ingress domain, the script will print out instructions for
configuring DNS for the domain. You will need to add the wildcard CNAME record
the script generates. Note that not all registrars permit wildcard CNAME
records. One registrar that we know supports wildcard CNAME records is
[Namecheap](https://www.namecheap.com/), which allows the addition of CNAME
records via the "Advanced DNS" section of a domain's control panel.

**IMPORTANT**: The trailing dot at the end of the CNAME target is required. The
record will not work without it.

### Testing DNS Propagation

Next, the script will ask if you would like to test DNS propagation. This is
completely optional and does not affect the operation of your cluster in any
way. If you say "yes", the script will check for a CNAME record at
`*.<your ingress domain>` every 60 seconds to see if it matches the expected
CNAME record. It will continue checking until it finds a match. After each
failed check, it will print a reason for the failure (missing record, or
misconfigured record).

In our tests, propagation generally takes only a few minutes. However, it can
theoretically take much longer than that. While waiting for propagation, you
should expect to see failure messages from the DNS propagation checker.

Regardless of whether or not you use the builtin propagation checker, you will
need to wait for the CNAME record to propagate to your computer before you can
run supercluster.

### Supercluster Options

At this point you should see a message stating "Your cluster is ready!". Below
that message are a set of flags you must add to any supercluster runs you wish
to perform.

### Shutting Down

The last thing the script prints is the `eksctl` command you need to use to shut
down your cluster when you are done using it. In case you closed your terminal
window after running the script, the command is:

```bash
eksctl delete cluster --name <cluster-name> --region <aws-region>
```