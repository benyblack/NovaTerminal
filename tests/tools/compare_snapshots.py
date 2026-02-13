#!/usr/bin/env python3
import os
import sys
import json
import difflib

def load_snapshots(directory):
    snapshots = {}
    if not os.path.exists(directory):
        print(f"Warning: Directory {directory} does not exist.")
        return snapshots
    
    for root, _, files in os.walk(directory):
        for file in files:
            if file.endswith('.snap'):
                rel_path = os.path.relpath(os.path.join(root, file), directory)
                with open(os.path.join(root, file), 'r', encoding='utf-8') as f:
                    snapshots[rel_path] = f.read()
    return snapshots

def compare(dirs):
    all_snapshots = [load_snapshots(d) for d in dirs]
    
    # Get union of all keys
    all_keys = set()
    for s in all_snapshots:
        all_keys.update(s.keys())
    
    if not all_keys:
        print("FAIL: No snapshots found to compare.")
        return False

    success = True
    for key in sorted(all_keys):
        contents = [s.get(key) for s in all_snapshots]
        
        # Check if any platform is missing this snapshot
        missing = [i for i, c in enumerate(contents) if c is None]
        if missing:
            print(f"FAIL: Snapshot '{key}' is missing on platforms: {[dirs[i] for i in missing]}")
            success = False
            continue

        # Compare all to the first one
        base = contents[0]
        for i in range(1, len(contents)):
            if contents[i] != base:
                print(f"FAIL: Disparity in '{key}' between {dirs[0]} and {dirs[i]}")
                
                # Show diff
                diff = difflib.unified_diff(
                    base.splitlines(),
                    contents[i].splitlines(),
                    fromfile=dirs[0],
                    tofile=dirs[i],
                    lineterm=''
                )
                print('\n'.join(diff))
                success = False

    return success

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: compare_snapshots.py dir1 dir2 [dir3...]")
        sys.exit(1)
    
    if compare(sys.argv[1:]):
        print("SUCCESS: All snapshots match exactly across platforms.")
        sys.exit(0)
    else:
        print("FAILURE: Snapshot disparities detected.")
        sys.exit(1)
