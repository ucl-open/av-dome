# Create a new repository from `rig-template` using Copier

This guide explains how to initialize and create a new open-ucl repository from the GitHub template
`ucl-open/rig-template` using the **Copier** CLI. This tool allows you to recreate the base components of an open-ucl project, including the base Bonsai and Python environments required to get started.

All powershell commands can be run from the command line or from a terminal in VSCode.

Template source:  
https://github.com/ucl-open/rig-template.git

Copier documentation:  
https://copier.readthedocs.io/en/stable/

---

## Prerequisites

Ensure the following are installed:

- **Windows 10 or newer**
- **Python >3.11 + <3.12**
  - Download from https://www.python.org/downloads/windows/
  - Make sure **“Add Python to PATH”** is checked during installation
- **Git 2.27+**
  - Download from https://git-scm.com/download/win

---

## Install `uv`

`uv` is a very fast package and tool manager for Python. We recommend this for all your Python needs

Install it using a command line terminal:

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
```

Restart your terminal before continuing, so that we can pick up the new paths

---

## Install Copier using `uv`

In a command line terminal, install Copier as a global CLI tool:

```powershell
uv tool install copier
```
---

## Create a new project from the template

### 1. Choose the template branch

Decide which branch (or tag) of the template you want to use, for example:

- `main`
- `dev`
- `v1.0.0`

---

### 2. Generate the project

Run Copier from the directory where you want the new project folder created:

Example:

```powershell
copier copy --vcs-ref main https://github.com/ucl-open/rig-template.git ./my-new-project
```
the --vcs-ref flag allows you to specify a given branch or tag. Strictly speaking you do not need to point to main, you can instead omit `--vcs-ref main`
Copier will prompt you for some basic template variables and then generate the files and directory structure.

---

## Initialize your new Git repository

### 3. Create an empty repository for your new project
Using a browser, create a new, empty repository on GitHub (do not add a README or license, the template will sort that).

### 4. Initialize Git with your chosen default branch
1. Open an instance of VSCode and open your new project folder
2. Initialize a local git repository:

```powershell
git init -b main
```
3. Add the remote and push:

```powershell
git remote add origin https://github.com/ucl-open/my-new-project.git
git push -u origin main
```

### Congrats! 
You have created a new project with all open-ucl dependencies and base components to begin your new rig or experiment project!

---
