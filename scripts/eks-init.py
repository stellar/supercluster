#!/usr/bin/env python3

import glob
import json
import os
import subprocess
import time

# eksctl constants
DEFAULT_NAME = "ssc"
DEFAULT_REGION = "us-west-2"
# DEFAULT_NUM_NODES should be the number of nodes in the theoretical max TPS
# topology plus 3 to cover the ingress controller and two coredns instances.
DEFAULT_NUM_NODES = 10
DEFAULT_NODE_TYPE = "m5d.4xlarge"
KUBERNETES_VERSION = "1.31"
SSH_USERNAME = "ec2-user"
NVME_MOUNT_SCRIPT = "https://raw.githubusercontent.com/awslabs/amazon-eks-ami/86105105a83fdc80e682b850aac6f626119a6951/templates/shared/runtime/bin/setup-local-disks"
DNS_RETRY_INTERVAL_SECONDS = 60

def run(command):
    """ Run a command and exit if it fails. Prints the command's output. """
    print(f"Running: {command}")
    res = os.system(command)
    if res != 0:
        print(f"Command '{command}' failed with exit code {res}")
        exit(1)

def run_capture_output(command):
    """ Run a command and exit if it fails. Returns the command's output. """
    try:
        return subprocess.check_output(command)
    except subprocess.CalledProcessError as e:
        print(f"Command '{command}' failed with exit code {e.returncode}")
        exit(1)

def prompt_with_default(prompt, default):
    """ Prompt the user for input with a default value. """
    res = input(f"{prompt} [{default}]: ")
    return res if res else default

def prompt_yes_no(prompt, default):
    """ Prompt the user for a yes/no answer with a default value. """
    full_prompt = f"{prompt} [Y/n]: " if default else f"{prompt} [y/N]: "
    res = input(full_prompt)
    return res.lower() in ["y", "yes"] if res else default

def prompt_ssh_public_key():
    """
    Prompt the user for an SSH public key. Attempts to find a default key to
    suggest.
    """
    # Look for a key to use by default
    prompt = "SSH public key"
    keys = glob.glob(os.path.expanduser("~/.ssh/*.pub"))
    if keys:
        return prompt_with_default(prompt, keys[0])
    return input(f"{prompt}: ")

def create_cluster(name, region, num_nodes, node_type, ssh_public_key):
    """ Create an EKS cluster with the given parameters. """
    run( "eksctl create cluster "
        f"--name {name} "
        f"--region {region} "
        f"--nodes {num_nodes} "
        f"--node-type {node_type} "
        f"--version {KUBERNETES_VERSION} "
         "--ssh-access "
        f"--ssh-public-key {ssh_public_key} "
        )

def node_is_in_cluster(cluster_name, node):
    """
    Check if a node is in the given cluster. `node` must be an `Instance` object
    from the `aws ec2 describe-instances` command.
    """
    if "Tags" not in node.keys():
        return False

    if node["State"]["Name"] != "running":
        # describe-instances may return recently terminated nodes from a
        # previous version of the cluster. This check ensures those are
        # discarded.
        return False

    tags = node["Tags"]
    for tag in tags:
        try:
            if (tag["Key"] == "eks:cluster-name" and
                tag["Value"] == cluster_name):
                return True
        except KeyError:
            pass
    return False


def get_node_ips(region, cluster_name):
    """
    Get the public IP addresses of the nodes in the given cluster. Returns a
    list of IPs as strings of dotted quads.
    """
    reservations = json.loads(run_capture_output(
        ["aws", "--region", region, "ec2", "describe-instances",
         "--output", "json", "--no-paginate"
        ]))
    ips = []
    for reservation in reservations["Reservations"]:
        for instance in reservation["Instances"]:
            if node_is_in_cluster(cluster_name, instance):
                ips.append(instance["PublicIpAddress"])
    return ips

def mount_nvme(ips, public_key):
    """
    Mount NVMe drives on the given instances. `ips` is a list of IP addresses to
    SSH into for the purposes of mounting the drives. `public_key` is the path
    to the public key to use for SSH.
    """
    private_key = prompt_with_default("SSH private key",
                                      public_key.replace(".pub", ""))
    command = f"wget {NVME_MOUNT_SCRIPT} && sudo bash setup-local-disks raid0"
    for ip in ips:
        print(f"Mounting NVMe drives on {ip}...")
        run(f"ssh -o StrictHostKeyChecking=no -i {private_key} "
            f"{SSH_USERNAME}@{ip} '{command}'")
        print()

def setup_ingress_controller():
    """
    Setup an nginx ingress controller in the cluster. This implicitly spins up
    an AWS Classic Load Balancer.
    """
    run("helm repo add ingress-nginx "
        "https://kubernetes.github.io/ingress-nginx")
    run("helm upgrade "
        "--install ingress-nginx ingress-nginx "
        "--repo https://kubernetes.github.io/ingress-nginx "
        "--namespace ingress-nginx "
        "--create-namespace")

def emit_dns_instructions(domain):
    """
    Prints instructions for adding wildcard CNAME record for the given domain.
    Returns the target hostname to use in the CNAME record.
    """
    service = json.loads(run_capture_output(
        ["kubectl", "get", "services", "ingress-nginx-controller",
         "-n", "ingress-nginx",
         "-o", "json"
        ]))
    hostname = service["status"]["loadBalancer"]["ingress"][0]["hostname"]
    print()
    print("Before running supercluster, you must add a CNAME record for "
          f"'*.{domain}' pointing to '{hostname}.'.")
    print("Important considerations:")
    print("* Your registrar must support wildcard CNAME records.")
    print("* The trailing dot in the target hostname is important.")
    print("* DNS propagation may take some time. In many cases it will take ")
    print("  on the order of minutes, but it can take significantly longer.")
    return hostname

def test_dns_propagation(domain, target):
    """
    Block until the CNAME record for `*.domain` points to `target`.
    """
    lookup_domain = f"*.{domain}"
    while True:
        record = run_capture_output([
            "dig", "+noall", "+answer", lookup_domain, "CNAME"
        ]).decode("utf-8")
        if record:
            print("Found CNAME record:")
            print(record)
            if target in record:
                print("DNS propagation successful!")
                break
            else:
                print(f"CNAME record target does not match "
                      f"expected target '{target}'. Checking again in "
                      f"{DNS_RETRY_INTERVAL_SECONDS} seconds.")
        else:
            print(f"No CNAME record found. Checking again in "
                  f"{DNS_RETRY_INTERVAL_SECONDS} seconds.")
        time.sleep(DNS_RETRY_INTERVAL_SECONDS)

def main():
    """ Interactively create and initialize an EKS cluster """
    cluster_name = prompt_with_default("Cluster name", DEFAULT_NAME)
    region = prompt_with_default("Region", DEFAULT_REGION)
    ssh_public_key = prompt_ssh_public_key()
    num_nodes = None
    if prompt_yes_no("Create EKS cluster", True):
        num_nodes = prompt_with_default("Number of nodes", DEFAULT_NUM_NODES)
        node_type = prompt_with_default("Node type", DEFAULT_NODE_TYPE)
        print("Creating EKS cluster...")
        create_cluster(cluster_name,
                       region,
                       num_nodes,
                       node_type,
                       ssh_public_key)

    ips = get_node_ips(region, cluster_name)
    print(f"Cluster instance IP addresses: {ips}")
    if num_nodes is not None and len(ips) != int(num_nodes):
        print("Error: The number of IP addresses found does not match the "
              "number of requested nodes.")
        exit(1)

    if prompt_yes_no("Mount NVMe drives on cluster instances", True):
        mount_nvme(ips, ssh_public_key)

    if prompt_yes_no("Install ingress controller", True):
        print("Setting up ingress controller...")
        setup_ingress_controller()

    ingress_domain = input("Ingress domain: ")
    cname_target = emit_dns_instructions(ingress_domain)

    if prompt_yes_no("Test DNS propagation", True):
        test_dns_propagation(ingress_domain, cname_target)

    print()
    print("Your cluster is ready!")
    print("Add the following flags to your supercluster runs:")
    print(f"--ingress-domain {ingress_domain} "
          "--ingress-class nginx "
          "--namespace default")

    print()
    print("To shut down the cluster, run "
          f"'eksctl delete cluster --name {cluster_name} --region {region}'")

if __name__ == "__main__":
    main()
