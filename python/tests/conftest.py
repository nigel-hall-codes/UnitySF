"""Make the ``sfmap`` package importable when tests run from any cwd."""
import os
import sys

# python/ (parent of tests/) holds the sfmap package.
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
